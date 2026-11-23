using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trace.Application.Retrieval;
using Trace.Application.Telemetry;
using Trace.Domain;

namespace Trace.Application.Investigations;

/// <summary>Why the conversation with the model ended.</summary>
internal enum ConversationEnd
{
    /// <summary>The model gave a final turn without tool calls.</summary>
    Final,

    TokenLimit,

    StepLimit,
}

/// <summary>Mutable state of one investigation run.</summary>
internal sealed class InvestigationRun(Investigation investigation, string commitSha, Activity? activity = null)
{
    private readonly Dictionary<string, HashSet<int>> seenLines = new(StringComparer.Ordinal);

    public Investigation Investigation { get; } = investigation;

    public string CommitSha { get; } = commitSha;

    /// <summary>The span of this run, if anyone is listening.</summary>
    public Activity? Activity { get; } = activity;

    public int EventSeq { get; set; }

    public int ToolCalls { get; set; }

    public int ToolOutputChars { get; set; }

    public void MarkSeen(string path, int startLine, int endLine)
    {
        if (!seenLines.TryGetValue(path, out var lines))
        {
            seenLines[path] = lines = [];
        }

        lines.UnionWith(Enumerable.Range(startLine, endLine - startLine + 1));
    }

    public bool HasSeen(string path, int startLine, int endLine) =>
        seenLines.TryGetValue(path, out var lines)
        && Enumerable.Range(startLine, endLine - startLine + 1).All(lines.Contains);
}

/// <summary>
/// What every kind of investigation shares: the run around the work (budget, start, failure
/// names, telemetry), the bounded conversation with the model, the two read-only tools, and the
/// rule that decides whether a claimed citation counts.
/// <para>
/// The model only proposes. The repository and snapshot of every tool call come from the
/// investigation, never from the model; anything else the model asks for is refused; and a
/// claimed citation counts only if it exists in the snapshot and its lines were actually
/// returned to the model during this run. A question and a plan differ in the prompt, in what
/// the first message holds and in what is made of the final turn: nothing else.
/// </para>
/// </summary>
internal sealed class InvestigationRunner(
    ISnapshotReader snapshots,
    IInvestigationStore store,
    SourceTools tools,
    CitationValidator citationValidator,
    IChatModel model,
    ChatLimits limits,
    TimeProvider timeProvider)
{
    public const int MaxModelTurns = 6;
    public const int MaxToolCalls = 8;
    public const int MaxSearchHitsPerCall = 8;
    public const int DefaultSearchHits = 5;
    public const int FullTextHitsPerSearch = 5;
    public const int MaxPreviewLength = 160;

    public const string SearchNote =
        "Hits with previewOnly show a single line: call read_file_range for their text before relying on or citing them. "
        + "Hits with shownEarlier were returned in full earlier in this conversation and are not repeated.";

    public const int MaxToolOutputChars = 120_000;

    private static readonly JsonSerializerOptions ToolArgumentJson = new(JsonSerializerDefaults.Web)
    {
        // A tool call may carry only its documented arguments: a model cannot pass, for
        // example, a repository or snapshot ID of its own choosing.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions ToolResultJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// For tool results sent to the model. The default encoder writes every quote, angle
    /// bracket, ampersand, plus sign and non-ASCII character of source code as a six-character
    /// escape, which costs several tokens each and makes the code harder to read. That
    /// escaping protects JSON pasted into HTML; these strings only ever go to the model API.
    /// </summary>
    private static readonly JsonSerializerOptions ModelFacingJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ModelId => model.ModelId;

    /// <summary>
    /// Runs a stored investigation of the given type to a final state. Safe to call again after
    /// an interrupted attempt: a finished investigation is left alone, an unfinished one starts
    /// over (evidence is only ever stored on completion, so nothing is duplicated).
    /// <para>
    /// <paramref name="investigate"/> does the work once the investigation has started and
    /// returns a fixed name for how it ended. Cancellation and unexpected errors are rethrown
    /// for the job queue to decide (cancel, retry or give up); <see cref="AbandonAsync"/> then
    /// records the final state.
    /// </para>
    /// </summary>
    public async Task RunAsync(
        Guid investigationId,
        InvestigationType type,
        string promptVersion,
        int attempt,
        int maxAttempts,
        Func<InvestigationRun, CancellationToken, Task<string>> investigate,
        CancellationToken cancellationToken)
    {
        using var activity = TraceTelemetry.Source.StartActivity("investigation.run");
        activity?.SetTag("trace.investigation.id", investigationId);
        activity?.SetTag("trace.investigation.attempt", attempt);
        var started = timeProvider.GetTimestamp();
        var outcome = "error";
        try
        {
            outcome = await RunCoreAsync(
                investigationId, type, promptVersion, attempt, maxAttempts, activity, investigate, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            activity?.SetTag("trace.investigation.outcome", outcome);
            if (outcome is not ("answered" or "planned" or "insufficient_evidence" or "skipped"))
            {
                TraceTelemetry.Fail(activity, outcome);
            }

            TraceTelemetry.InvestigationDuration.Record(
                timeProvider.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("trace.investigation.outcome", outcome));
        }
    }

    private async Task<string> RunCoreAsync(
        Guid investigationId,
        InvestigationType type,
        string promptVersion,
        int attempt,
        int maxAttempts,
        Activity? activity,
        Func<InvestigationRun, CancellationToken, Task<string>> investigate,
        CancellationToken cancellationToken)
    {
        var investigation = await store.FindForUpdateAsync(investigationId, cancellationToken);
        if (investigation is null || investigation.IsFinished)
        {
            return "skipped";
        }

        if (investigation.Type != type)
        {
            // A job of one kind was pointed at an investigation of another: never run it under
            // the wrong prompt and the wrong rules.
            throw new Jobs.JobFailedException(
                Jobs.JobErrors.InvalidPayload, "The investigation is not of the type this job runs.");
        }

        activity?.SetTag("trace.repository.id", investigation.RepositoryId);
        activity?.SetTag("gen_ai.request.model", model.ModelId);

        var snapshot = await snapshots.FindSnapshotAsync(
            investigation.RepositoryId, investigation.SnapshotId, cancellationToken)
            ?? throw new InvalidOperationException("The investigation's snapshot no longer exists.");

        var run = new InvestigationRun(investigation, snapshot.CommitSha, activity)
        {
            EventSeq = await store.GetLastEventSeqAsync(investigation.Id, cancellationToken),
        };
        var spentToday = await store.SumTokensSinceAsync(timeProvider.GetUtcNow() - TimeSpan.FromHours(24), cancellationToken);
        if (spentToday >= limits.DailyTokenBudget)
        {
            // Fail closed: do not call the model at all once the shared budget is used up.
            await FailAsync(run, "budget_exhausted", "Failed: the daily model budget has been used up.");
            return "budget_exhausted";
        }

        investigation.Start(model.ModelId, promptVersion);
        await store.SaveAsync(investigation, [], cancellationToken);
        await EmitAsync(
            run, InvestigationEventType.Started,
            attempt <= 1
                ? $"Started investigating at commit {snapshot.CommitSha[..12]}."
                : $"Started again (attempt {attempt}) at commit {snapshot.CommitSha[..12]}.",
            // Lets someone holding an investigation find its trace and its log lines.
            metadataJson: JsonSerializer.Serialize(
                new { correlationId = TraceTelemetry.CorrelationId, traceId = activity?.TraceId.ToString() }, ToolResultJson),
            cancellationToken: cancellationToken);

        try
        {
            return await investigate(run, cancellationToken);
        }
        catch (InvalidModelOutputException exception)
        {
            // Asking again would cost another model run for an answer we already could not use.
            await FailAsync(run, "invalid_model_output", $"Failed: {exception.Message}");
            return "invalid_model_output";
        }
        catch (ChatModelRefusedException)
        {
            await FailAsync(run, "model_refused", "Failed: the language model declined to respond to this request.");
            return "model_refused";
        }
        catch (ChatModelException) when (attempt >= maxAttempts)
        {
            await FailAsync(run, "model_error", "Failed: the language model could not be reached.");
            return "model_error";
        }
    }

    /// <summary>Records on the run's span what a completed investigation used and found.</summary>
    public static void TagCompleted(InvestigationRun run, int evidenceCount)
    {
        run.Activity?.SetTag("gen_ai.usage.input_tokens", run.Investigation.InputTokens);
        run.Activity?.SetTag("gen_ai.usage.output_tokens", run.Investigation.OutputTokens);
        run.Activity?.SetTag("trace.investigation.tool_calls", run.ToolCalls);
        run.Activity?.SetTag("trace.investigation.evidence", evidenceCount);
    }

    /// <summary>
    /// Records that an investigation's job ended without finishing it: cancelled on request, or
    /// failed for good. Does nothing if the investigation already has a final state.
    /// </summary>
    public async Task AbandonAsync(
        Guid investigationId,
        bool cancelled,
        string? jobErrorCode,
        CancellationToken cancellationToken = default)
    {
        var investigation = await store.FindForUpdateAsync(investigationId, cancellationToken);
        if (investigation is null || investigation.IsFinished)
        {
            return;
        }

        var run = new InvestigationRun(investigation, commitSha: string.Empty)
        {
            EventSeq = await store.GetLastEventSeqAsync(investigation.Id, cancellationToken),
        };

        if (cancelled)
        {
            await EmitAsync(run, InvestigationEventType.Cancelled, "Cancelled on request.", cancellationToken: cancellationToken);
            investigation.Cancel();
        }
        else
        {
            var (errorCode, summary) = jobErrorCode switch
            {
                Jobs.JobErrors.AttemptsExhausted => ("worker_interrupted", "Failed: the worker stopped repeatedly before finishing."),
                Jobs.JobProcessor.TimeoutError => ("timed_out", "Failed: the investigation took too long."),
                _ => ("internal_error", "Failed unexpectedly."),
            };
            await EmitAsync(run, InvestigationEventType.Failed, summary, cancellationToken: cancellationToken);
            investigation.Fail(errorCode);
        }

        await store.SaveAsync(investigation, [], cancellationToken);
    }

    /// <summary>
    /// The bounded conversation: the model is called at most <see cref="MaxModelTurns"/> times
    /// and never once the investigation's token limit is reached. The system prompt and the
    /// tool list are the caller's constants; nothing the model or the first message says can
    /// change them, the repository, the snapshot or a limit.
    /// </summary>
    public async Task<(ConversationEnd End, ModelTurn? FinalTurn)> ConverseAsync(
        InvestigationRun run,
        string systemPrompt,
        IReadOnlyList<ToolDefinition> toolDefinitions,
        string firstMessage,
        CancellationToken cancellationToken)
    {
        var messages = new List<ModelMessage> { new UserMessage(firstMessage) };

        for (var turnNumber = 1; turnNumber <= MaxModelTurns; turnNumber++)
        {
            if (run.Investigation.InputTokens + run.Investigation.OutputTokens >= limits.MaxTokensPerInvestigation)
            {
                return (ConversationEnd.TokenLimit, null);
            }

            var turn = await CompleteAsync(systemPrompt, toolDefinitions, messages, cancellationToken);
            if (turn.Usage is { } usage)
            {
                // Saved after every call so spending is counted even if this attempt dies.
                run.Investigation.RecordUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens);
                await store.SaveAsync(run.Investigation, [], cancellationToken);
            }

            if (turn.ToolCalls.Count == 0)
            {
                return (ConversationEnd.Final, turn);
            }

            messages.Add(new AssistantToolCallsMessage(turn.ToolCalls, turn.ProviderState));
            foreach (var call in turn.ToolCalls)
            {
                messages.Add(await ExecuteToolAsync(run, call, cancellationToken));
            }
        }

        return (ConversationEnd.StepLimit, null);
    }

    /// <summary>
    /// Decides which claimed citations count. The result has one entry per claim, in order: the
    /// check of an accepted citation (with the text read from the snapshot), or null for one
    /// that was discarded. Every discarded citation is recorded as an event.
    /// </summary>
    public async Task<IReadOnlyList<CitationCheck?>> ValidateCitationsAsync(
        InvestigationRun run,
        IReadOnlyList<Citation> claimed,
        CancellationToken cancellationToken)
    {
        var checks = await citationValidator.ValidateAsync(
            run.Investigation.RepositoryId, run.Investigation.SnapshotId, claimed, cancellationToken);

        var accepted = new List<CitationCheck?>(claimed.Count);
        for (var index = 0; index < claimed.Count; index++)
        {
            var citation = checks[index].Citation;
            var location = $"{Clean(citation.Path, 120)}:{citation.StartLine}-{citation.EndLine}";

            string? rejection = checks[index] switch
            {
                { IsValid: false } => "it does not exist in the indexed snapshot",
                _ when !run.HasSeen(citation.Path, citation.StartLine, citation.EndLine) =>
                    "its lines were not retrieved during this investigation",
                { Excerpt: var excerpt } when string.IsNullOrWhiteSpace(excerpt) => "it cites only blank lines",
                _ => null,
            };

            if (rejection is not null)
            {
                await EmitAsync(
                    run, InvestigationEventType.CitationRejected,
                    $"Discarded a citation to {location}: {rejection}.", cancellationToken: cancellationToken);
                accepted.Add(null);
                continue;
            }

            accepted.Add(checks[index]);
        }

        return accepted;
    }

    /// <summary>The stored evidence row for an accepted citation.</summary>
    public static EvidenceRef EvidenceFor(InvestigationRun run, CitationCheck accepted)
    {
        var citation = accepted.Citation;
        return EvidenceRef.ForCode(
            run.Investigation,
            accepted.ChunkId!.Value,
            JsonSerializer.Serialize(
                new { citation.Path, citation.StartLine, citation.EndLine, commitSha = run.CommitSha }, ToolResultJson),
            accepted.Excerpt!);
    }

    public async Task FailAsync(InvestigationRun run, string errorCode, string summary)
    {
        await EmitAsync(run, InvestigationEventType.Failed, summary, cancellationToken: CancellationToken.None);
        run.Investigation.Fail(errorCode);
        await store.SaveAsync(run.Investigation, [], CancellationToken.None);
    }

    public Task EmitAsync(
        InvestigationRun run,
        InvestigationEventType eventType,
        string summary,
        string? toolName = null,
        string? metadataJson = null,
        CancellationToken cancellationToken = default) =>
        store.AppendEventAsync(
            InvestigationEvent.Create(
                run.Investigation.Id, ++run.EventSeq, eventType,
                Clean(summary, InvestigationEvent.MaxSummaryLength), toolName, metadataJson),
            cancellationToken);

    /// <summary>Makes model- or repository-supplied text safe for a one-line summary: no control characters, bounded length.</summary>
    public static string Clean(string? text, int maxLength)
    {
        var cleaned = new string([.. (text ?? string.Empty).Select(character => char.IsControl(character) ? ' ' : character)]).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..(maxLength - 1)] + "…";
    }

    private async Task<ModelTurn> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ToolDefinition> toolDefinitions,
        List<ModelMessage> messages,
        CancellationToken cancellationToken)
    {
        using var activity = TraceTelemetry.Source.StartActivity("model.turn", ActivityKind.Client);
        var modelTag = new KeyValuePair<string, object?>("gen_ai.request.model", model.ModelId);
        activity?.SetTag(modelTag.Key, modelTag.Value);
        var started = timeProvider.GetTimestamp();
        var outcome = "error";
        try
        {
            // A snapshot of the conversation: the model must not be able to mutate our history.
            var request = new ModelRequest(systemPrompt, [.. messages], toolDefinitions);
            var turn = await model.CompleteAsync(request, cancellationToken)
                ?? throw new ChatModelException("The chat model returned no turn.");

            outcome = turn.ToolCalls.Count > 0 ? "tool_calls" : "final";
            activity?.SetTag("trace.model.tool_calls", turn.ToolCalls.Count);
            if (turn.Usage is { } usage)
            {
                activity?.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
                activity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
                activity?.SetTag("gen_ai.usage.cache_read.input_tokens", usage.CacheReadTokens);
                activity?.SetTag("gen_ai.usage.cache_creation.input_tokens", usage.CacheWriteTokens);
                TraceTelemetry.ModelTokens.Add(usage.CacheReadTokens, modelTag, new("gen_ai.token.type", "cache_read"));
                TraceTelemetry.ModelTokens.Add(usage.InputTokens, modelTag, new("gen_ai.token.type", "input"));
                TraceTelemetry.ModelTokens.Add(usage.OutputTokens, modelTag, new("gen_ai.token.type", "output"));
            }

            return turn;
        }
        catch (ChatModelRefusedException)
        {
            outcome = "model_refused";
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception exception) when (exception is not ChatModelException)
        {
            throw new ChatModelException("The chat model call failed.", exception);
        }
        finally
        {
            if (outcome is not ("tool_calls" or "final"))
            {
                TraceTelemetry.Fail(activity, outcome);
            }

            TraceTelemetry.ModelTurnDuration.Record(
                timeProvider.GetElapsedTime(started).TotalSeconds,
                modelTag,
                new KeyValuePair<string, object?>("trace.model.outcome", outcome));
        }
    }

    private async Task<ToolResultMessage> ExecuteToolAsync(InvestigationRun run, ToolCall call, CancellationToken cancellationToken)
    {
        if (call.Name is not (QuestionPrompt.SearchCode or QuestionPrompt.ReadFileRange))
        {
            await EmitAsync(
                run, InvestigationEventType.ToolRejected,
                $"Refused a request for a tool that is not available: \"{Clean(call.Name, 80)}\".",
                cancellationToken: cancellationToken);
            return ToolError(call, "unknown_tool", "This tool does not exist. Only search_code and read_file_range are available.");
        }

        if (run.ToolCalls >= MaxToolCalls)
        {
            await EmitAsync(
                run, InvestigationEventType.ToolRejected,
                $"Refused a {call.Name} call: the investigation's limit of {MaxToolCalls} tool calls was reached.",
                call.Name, cancellationToken: cancellationToken);
            return ToolError(call, "budget_exhausted", "No more tool calls are allowed. Answer with what you have.");
        }

        run.ToolCalls++;
        var stopwatch = Stopwatch.StartNew();

        // The tool name is one of two fixed values here; its arguments are never recorded.
        using var activity = TraceTelemetry.Source.StartActivity($"tool {call.Name}");
        var toolTag = new KeyValuePair<string, object?>("trace.tool.name", call.Name);
        activity?.SetTag(toolTag.Key, toolTag.Value);
        var toolOutcome = "ok";
        try
        {
            var (content, summary, resultCount, seen) = call.Name == QuestionPrompt.SearchCode
                ? await SearchAsync(run, call, cancellationToken)
                : await ReadAsync(run, call, cancellationToken);

            if (run.ToolOutputChars + content.Length > MaxToolOutputChars)
            {
                await EmitAsync(
                    run, InvestigationEventType.ToolRejected,
                    $"Withheld the result of a {call.Name} call: the investigation's tool output limit was reached.",
                    call.Name, cancellationToken: cancellationToken);
                toolOutcome = "budget_exhausted";
                return ToolError(call, "budget_exhausted", "The tool output limit was reached. Answer with what you have.");
            }

            activity?.SetTag("trace.tool.result_count", resultCount);

            run.ToolOutputChars += content.Length;
            foreach (var (path, startLine, endLine) in seen)
            {
                run.MarkSeen(path, startLine, endLine);
            }

            await EmitAsync(
                run, InvestigationEventType.ToolCalled, summary, call.Name,
                JsonSerializer.Serialize(
                    new { latencyMs = stopwatch.ElapsedMilliseconds, resultCount, outputChars = content.Length }, ToolResultJson),
                cancellationToken);
            return new ToolResultMessage(call.Id, call.Name, content, IsError: false);
        }
        catch (SourceToolException exception)
        {
            var code = JsonNamingPolicy.SnakeCaseLower.ConvertName(exception.Error.ToString());
            toolOutcome = code;
            await EmitAsync(
                run, InvestigationEventType.ToolFailed,
                $"A {call.Name} call returned an error: {exception.Message}", call.Name,
                JsonSerializer.Serialize(new { latencyMs = stopwatch.ElapsedMilliseconds, error = code }, ToolResultJson),
                cancellationToken);
            return ToolError(call, code, exception.Message);
        }
        finally
        {
            if (toolOutcome != "ok")
            {
                TraceTelemetry.Fail(activity, toolOutcome);
            }

            TraceTelemetry.ToolDuration.Record(
                stopwatch.Elapsed.TotalSeconds, toolTag, new KeyValuePair<string, object?>("trace.tool.outcome", toolOutcome));
        }
    }

    private async Task<ToolOutput> SearchAsync(InvestigationRun run, ToolCall call, CancellationToken cancellationToken)
    {
        var arguments = ParseArguments<SearchArguments>(call);
        var limit = Math.Clamp(arguments.Limit ?? DefaultSearchHits, 1, MaxSearchHitsPerCall);

        var result = await tools.SearchCodeAsync(
            run.Investigation.RepositoryId, run.Investigation.SnapshotId, arguments.Query!, limit, cancellationToken);

        // Each passage is sent to the model in full at most once per investigation, and only
        // the best hits of a search are sent in full at all.
        var hits = new List<SearchHitOutput>(result.Hits.Count);
        var shown = new List<(string Path, int StartLine, int EndLine)>();
        foreach (var hit in result.Hits)
        {
            var location = (hit.Path, hit.StartLine, hit.EndLine);
            if (run.HasSeen(hit.Path, hit.StartLine, hit.EndLine))
            {
                hits.Add(new SearchHitOutput(hit.Path, hit.StartLine, hit.EndLine, hit.Language, hit.LexicalRank, hit.VectorRank, ShownEarlier: true));
            }
            else if (shown.Count < FullTextHitsPerSearch)
            {
                shown.Add(location);
                hits.Add(new SearchHitOutput(hit.Path, hit.StartLine, hit.EndLine, hit.Language, hit.LexicalRank, hit.VectorRank, Text: hit.Excerpt));
            }
            else
            {
                hits.Add(new SearchHitOutput(
                    hit.Path, hit.StartLine, hit.EndLine, hit.Language, hit.LexicalRank, hit.VectorRank,
                    Preview: Preview(hit.Excerpt), PreviewOnly: true));
            }
        }

        var abbreviated = hits.Any(hit => hit.PreviewOnly is true || hit.ShownEarlier is true);
        var content = JsonSerializer.Serialize(
            new SearchOutput(result.CommitSha, hits, abbreviated ? SearchNote : null), ModelFacingJson);

        // Only passages whose text was sent count as seen, so only those can be cited.
        return new ToolOutput(
            content,
            $"Searched the code for \"{Clean(arguments.Query!, 120)}\": {result.Hits.Count} result(s).",
            result.Hits.Count,
            shown);
    }

    /// <summary>The first line of a passage that has any text, shortened.</summary>
    private static string Preview(string excerpt)
    {
        var line = excerpt.Split('\n').Select(candidate => candidate.Trim()).FirstOrDefault(candidate => candidate.Length > 0)
            ?? string.Empty;
        return Clean(line, MaxPreviewLength);
    }

    private async Task<ToolOutput> ReadAsync(InvestigationRun run, ToolCall call, CancellationToken cancellationToken)
    {
        var arguments = ParseArguments<ReadArguments>(call);

        var range = await tools.ReadFileRangeAsync(
            run.Investigation.RepositoryId, run.Investigation.SnapshotId,
            arguments.Path!, arguments.StartLine, arguments.EndLine, cancellationToken);

        var content = JsonSerializer.Serialize(
            new
            {
                range.CommitSha,
                range.Path,
                range.StartLine,
                range.EndLine,
                lines = range.Lines.Select(line => new { n = line.Number, text = line.Text }),
            },
            ModelFacingJson);

        return new ToolOutput(
            content,
            $"Read {range.Path} lines {range.StartLine}-{range.EndLine}.",
            range.Lines.Count,
            [(range.Path, range.StartLine, range.EndLine)]);
    }

    private static TArguments ParseArguments<TArguments>(ToolCall call)
        where TArguments : class
    {
        try
        {
            return JsonSerializer.Deserialize<TArguments>(call.ArgumentsJson, ToolArgumentJson)
                ?? throw new JsonException("null arguments");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentNullException)
        {
            throw new SourceToolException(
                SourceToolError.InvalidArgument,
                "Arguments must be a JSON object containing only the tool's documented properties.");
        }
    }

    private static ToolResultMessage ToolError(ToolCall call, string code, string message) =>
        new(
            call.Id,
            call.Name,
            JsonSerializer.Serialize(new { error = new { code, message } }, ToolResultJson),
            IsError: true);

    private sealed record SearchArguments(string? Query, int? Limit);

    private sealed record SearchOutput(string CommitSha, IReadOnlyList<SearchHitOutput> Hits, string? Note);

    private sealed record SearchHitOutput(
        string Path,
        int StartLine,
        int EndLine,
        string Language,
        int? LexicalRank,
        int? VectorRank,
        string? Text = null,
        string? Preview = null,
        bool? PreviewOnly = null,
        bool? ShownEarlier = null);

    private sealed record ReadArguments(string? Path, int StartLine, int EndLine);

    private sealed record ToolOutput(
        string Content,
        string Summary,
        int ResultCount,
        IReadOnlyList<(string Path, int StartLine, int EndLine)> Seen);
}
