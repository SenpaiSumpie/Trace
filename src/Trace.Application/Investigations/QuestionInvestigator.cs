using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trace.Application.Retrieval;
using Trace.Application.Telemetry;
using Trace.Domain;

namespace Trace.Application.Investigations;

public sealed record AskQuestion(Guid RepositoryId, Guid SnapshotId, string Question, string? IdempotencyKey = null);

public enum InvestigationRequestError
{
    RepositoryNotFound,
    SnapshotNotFound,
    SnapshotNotReady,
    IdempotencyConflict,
}

public sealed class InvestigationRequestException(InvestigationRequestError error, string message) : Exception(message)
{
    public InvestigationRequestError Error { get; } = error;
}

/// <summary>
/// Answers a question about one pinned snapshot with a bounded tool loop.
/// <para>
/// The model only proposes: it may ask for the two read-only tools and finally claim an answer
/// with citations. This class decides. The repository and snapshot of every tool call come
/// from the investigation, never from the model; anything else the model asks for is refused;
/// and a claimed citation counts as evidence only if it exists in the snapshot and its lines
/// were actually returned to the model during this run.
/// </para>
/// </summary>
public sealed class QuestionInvestigator(
    IRepositoryCatalog repositories,
    ISnapshotReader snapshots,
    IInvestigationStore store,
    SourceTools tools,
    CitationValidator citationValidator,
    IChatModel model,
    ChatLimits limits,
    Access.ICurrentUser currentUser,
    TimeProvider timeProvider,
    Jobs.IJobSignal jobSignal)
{
    public const int MaxModelTurns = 6;
    public const int MaxToolCalls = 8;
    public const int MaxSearchHitsPerCall = 8;
    public const int DefaultSearchHits = 5;

    /// <summary>
    /// How many hits of one search are returned with their full text. Further hits come back as
    /// a one-line preview the model can open with read_file_range, so a wide search does not
    /// cost a full passage for every weak match.
    /// </summary>
    public const int FullTextHitsPerSearch = 5;

    public const int MaxPreviewLength = 160;

    public const string SearchNote =
        "Hits with previewOnly show a single line: call read_file_range for their text before relying on or citing them. "
        + "Hits with shownEarlier were returned in full earlier in this conversation and are not repeated.";

    /// <summary>Upper bound on the total characters of tool output given to the model in one investigation.</summary>
    public const int MaxToolOutputChars = 120_000;

    public const string InsufficientEvidenceAnswer =
        "TRACE could not find enough evidence in the indexed snapshot to answer this question.";

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

    /// <summary>
    /// Validates the request and stores a queued investigation together with the job that will
    /// run it, in one transaction. Nothing is investigated here.
    /// </summary>
    public async Task<InvestigationRecord> CreateAsync(AskQuestion command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!await repositories.IsVisibleAsync(command.RepositoryId, cancellationToken))
        {
            throw new InvestigationRequestException(
                InvestigationRequestError.RepositoryNotFound, "The repository does not exist.");
        }

        var snapshot = await RequireSnapshotAsync(command, cancellationToken);

        if (command.IdempotencyKey is not null
            && await store.FindByIdempotencyKeyAsync(command.RepositoryId, command.IdempotencyKey, cancellationToken)
                is { } earlier)
        {
            return await ReplayAsync(earlier, command, cancellationToken);
        }

        var investigation = Investigation.Create(
            snapshot, InvestigationType.Question, command.Question, command.IdempotencyKey, currentUser.UserId);
        var job = Job.Enqueue(
            InvestigationJobHandler.JobKind,
            Job.RepositoryScope(investigation.RepositoryId),
            dedupeKey: investigation.Id.ToString(),
            InvestigationJobHandler.Payload(investigation.Id),
            timeProvider.GetUtcNow());

        if (!await store.TryAddAsync(investigation, job, cancellationToken))
        {
            var winner = await store.FindByIdempotencyKeyAsync(
                command.RepositoryId, command.IdempotencyKey!, cancellationToken)
                ?? throw new InvalidOperationException("Idempotency key conflict without an existing investigation.");
            return await ReplayAsync(winner, command, cancellationToken);
        }

        jobSignal.Notify();

        return await store.GetAsync(investigation.Id, cancellationToken)
            ?? throw new InvalidOperationException("The investigation could not be read back.");
    }

    /// <summary>
    /// Runs a stored investigation to a final state. Safe to call again after an interrupted
    /// attempt: a finished investigation is left alone, an unfinished one starts over (evidence
    /// is only ever stored on completion, so nothing is duplicated).
    /// <para>
    /// Cancellation and unexpected errors are rethrown for the job queue to decide (cancel,
    /// retry or give up); <see cref="AbandonAsync"/> then records the final state.
    /// </para>
    /// </summary>
    public async Task RunAsync(Guid investigationId, int attempt, int maxAttempts, CancellationToken cancellationToken)
    {
        using var activity = TraceTelemetry.Source.StartActivity("investigation.run");
        activity?.SetTag("trace.investigation.id", investigationId);
        activity?.SetTag("trace.investigation.attempt", attempt);
        var started = timeProvider.GetTimestamp();
        var outcome = "error";
        try
        {
            outcome = await RunCoreAsync(investigationId, attempt, maxAttempts, activity, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            activity?.SetTag("trace.investigation.outcome", outcome);
            if (outcome is not ("answered" or "insufficient_evidence" or "skipped"))
            {
                TraceTelemetry.Fail(activity, outcome);
            }

            TraceTelemetry.InvestigationDuration.Record(
                timeProvider.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("trace.investigation.outcome", outcome));
        }
    }

    /// <summary>Runs the investigation and returns a fixed name for how the run ended.</summary>
    private async Task<string> RunCoreAsync(
        Guid investigationId,
        int attempt,
        int maxAttempts,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var investigation = await store.FindForUpdateAsync(investigationId, cancellationToken);
        if (investigation is null || investigation.IsFinished)
        {
            return "skipped";
        }

        activity?.SetTag("trace.repository.id", investigation.RepositoryId);
        activity?.SetTag("gen_ai.request.model", model.ModelId);

        var snapshot = await snapshots.FindSnapshotAsync(
            investigation.RepositoryId, investigation.SnapshotId, cancellationToken)
            ?? throw new InvalidOperationException("The investigation's snapshot no longer exists.");

        var run = new Run(investigation, snapshot.CommitSha)
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

        investigation.Start(model.ModelId, QuestionPrompt.Version);
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
            var (result, evidence) = await InvestigateAsync(run, investigation.Question, cancellationToken);

            await EmitAsync(
                run, InvestigationEventType.Completed,
                result.Outcome == InvestigationOutcome.Answered
                    ? $"Answered with {result.Evidence.Count} validated evidence reference(s)."
                    : "Reported that the evidence was insufficient to answer.",
                cancellationToken: cancellationToken);
            investigation.Complete(result.ToJson());
            await store.SaveAsync(investigation, evidence, cancellationToken);
            activity?.SetTag("gen_ai.usage.input_tokens", investigation.InputTokens);
            activity?.SetTag("gen_ai.usage.output_tokens", investigation.OutputTokens);
            activity?.SetTag("trace.investigation.tool_calls", run.ToolCalls);
            activity?.SetTag("trace.investigation.evidence", result.Evidence.Count);
            return result.Outcome == InvestigationOutcome.Answered ? "answered" : "insufficient_evidence";
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

        var run = new Run(investigation, commitSha: string.Empty)
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

    private async Task<IndexSnapshot> RequireSnapshotAsync(AskQuestion command, CancellationToken cancellationToken)
    {
        try
        {
            return await SnapshotAccess.RequireCompletedAsync(
                snapshots, command.RepositoryId, command.SnapshotId, cancellationToken);
        }
        catch (SourceToolException exception)
        {
            throw new InvestigationRequestException(
                exception.Error == SourceToolError.SnapshotNotReady
                    ? InvestigationRequestError.SnapshotNotReady
                    : InvestigationRequestError.SnapshotNotFound,
                exception.Message);
        }
    }

    private async Task<InvestigationRecord> ReplayAsync(
        Investigation earlier,
        AskQuestion command,
        CancellationToken cancellationToken)
    {
        if (earlier.SnapshotId != command.SnapshotId || earlier.Question != command.Question)
        {
            throw new InvestigationRequestException(
                InvestigationRequestError.IdempotencyConflict,
                "The idempotency key was already used for a different request.");
        }

        return await store.GetAsync(earlier.Id, cancellationToken)
            ?? throw new InvalidOperationException("The investigation could not be read back.");
    }

    private async Task<(InvestigationResult Result, IReadOnlyList<EvidenceRef> Evidence)> InvestigateAsync(
        Run run,
        string question,
        CancellationToken cancellationToken)
    {
        var messages = new List<ModelMessage> { new UserMessage(question) };
        ModelTurn? finalTurn = null;

        for (var turnNumber = 1; turnNumber <= MaxModelTurns; turnNumber++)
        {
            if (run.Investigation.InputTokens + run.Investigation.OutputTokens >= limits.MaxTokensPerInvestigation)
            {
                return (Insufficient([], ["The investigation reached its token limit before the model produced an answer."]), []);
            }

            var turn = await CompleteAsync(messages, cancellationToken);
            if (turn.Usage is { } usage)
            {
                // Saved after every call so spending is counted even if this attempt dies.
                run.Investigation.RecordUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens);
                await store.SaveAsync(run.Investigation, [], cancellationToken);
            }

            if (turn.ToolCalls.Count == 0)
            {
                finalTurn = turn;
                break;
            }

            messages.Add(new AssistantToolCallsMessage(turn.ToolCalls, turn.ProviderState));
            foreach (var call in turn.ToolCalls)
            {
                messages.Add(await ExecuteToolAsync(run, call, cancellationToken));
            }
        }

        if (finalTurn is null)
        {
            return (Insufficient([], ["The investigation reached its step limit before the model produced an answer."]), []);
        }

        var answer = ModelAnswer.Parse(finalTurn.FinalText);
        var (evidence, references) = await ValidateCitationsAsync(run, answer, cancellationToken);
        var unknowns = (answer.Unknowns ?? [])
            .Where(unknown => !string.IsNullOrWhiteSpace(unknown))
            .Take(ModelAnswer.MaxUnknowns)
            .Select(unknown => Clean(unknown, ModelAnswer.MaxShortTextLength))
            .ToList();

        if (!answer.SufficientEvidence)
        {
            return (Insufficient(unknowns, ["The model reported that the retrieved source did not answer the question."]), []);
        }

        if (evidence.Count == 0 || string.IsNullOrWhiteSpace(answer.Answer))
        {
            return (
                Insufficient(unknowns, ["The model's answer had no citation that could be verified against the snapshot, so it was discarded."]),
                []);
        }

        var result = new InvestigationResult(
            InvestigationResult.CurrentSchemaVersion,
            InvestigationOutcome.Answered,
            answer.Answer.Trim(),
            evidence,
            unknowns,
            Limitations: []);
        return (result, references);
    }

    private async Task<ModelTurn> CompleteAsync(List<ModelMessage> messages, CancellationToken cancellationToken)
    {
        using var activity = TraceTelemetry.Source.StartActivity("model.turn", ActivityKind.Client);
        var modelTag = new KeyValuePair<string, object?>("gen_ai.request.model", model.ModelId);
        activity?.SetTag(modelTag.Key, modelTag.Value);
        var started = timeProvider.GetTimestamp();
        var outcome = "error";
        try
        {
            // A snapshot of the conversation: the model must not be able to mutate our history.
            var request = new ModelRequest(QuestionPrompt.System, [.. messages], QuestionPrompt.Tools);
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

    private async Task<ToolResultMessage> ExecuteToolAsync(Run run, ToolCall call, CancellationToken cancellationToken)
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

    private async Task<ToolOutput> SearchAsync(Run run, ToolCall call, CancellationToken cancellationToken)
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

    private async Task<ToolOutput> ReadAsync(Run run, ToolCall call, CancellationToken cancellationToken)
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

    private async Task<(List<EvidenceItem> Evidence, List<EvidenceRef> References)> ValidateCitationsAsync(
        Run run,
        ModelAnswer answer,
        CancellationToken cancellationToken)
    {
        var claimed = (answer.Citations ?? [])
            .DistinctBy(citation => (citation.Path, citation.StartLine, citation.EndLine))
            .ToList();
        var checks = await citationValidator.ValidateAsync(
            run.Investigation.RepositoryId,
            run.Investigation.SnapshotId,
            [.. claimed.Select(citation => new Citation(citation.Path ?? string.Empty, citation.StartLine, citation.EndLine))],
            cancellationToken);

        var evidence = new List<EvidenceItem>();
        var references = new List<EvidenceRef>();
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
                continue;
            }

            var excerptText = checks[index].Excerpt!;
            evidence.Add(new EvidenceItem(
                citation.Path, citation.StartLine, citation.EndLine,
                Clean(claimed[index].Supports ?? string.Empty, ModelAnswer.MaxShortTextLength),
                excerptText));
            references.Add(EvidenceRef.ForCode(
                run.Investigation,
                checks[index].ChunkId!.Value,
                JsonSerializer.Serialize(
                    new { citation.Path, citation.StartLine, citation.EndLine, commitSha = run.CommitSha }, ToolResultJson),
                excerptText));
        }

        return (evidence, references);
    }

    private async Task FailAsync(Run run, string errorCode, string summary)
    {
        await EmitAsync(run, InvestigationEventType.Failed, summary, cancellationToken: CancellationToken.None);
        run.Investigation.Fail(errorCode);
        await store.SaveAsync(run.Investigation, [], CancellationToken.None);
    }

    private Task EmitAsync(
        Run run,
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

    private static InvestigationResult Insufficient(IReadOnlyList<string> unknowns, IReadOnlyList<string> limitations) =>
        new(
            InvestigationResult.CurrentSchemaVersion,
            InvestigationOutcome.InsufficientEvidence,
            InsufficientEvidenceAnswer,
            Evidence: [],
            unknowns,
            limitations);

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

    /// <summary>Makes model- or repository-supplied text safe for a one-line summary: no control characters, bounded length.</summary>
    private static string Clean(string? text, int maxLength)
    {
        var cleaned = new string([.. (text ?? string.Empty).Select(character => char.IsControl(character) ? ' ' : character)]).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..(maxLength - 1)] + "…";
    }

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

    /// <summary>Mutable state of one investigation run.</summary>
    private sealed class Run(Investigation investigation, string commitSha)
    {
        private readonly Dictionary<string, HashSet<int>> seenLines = new(StringComparer.Ordinal);

        public Investigation Investigation { get; } = investigation;

        public string CommitSha { get; } = commitSha;

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
}
