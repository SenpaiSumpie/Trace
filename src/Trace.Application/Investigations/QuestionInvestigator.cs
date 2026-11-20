using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trace.Application.Retrieval;
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
    IChatModel model)
{
    public const int MaxModelTurns = 6;
    public const int MaxToolCalls = 8;
    public const int MaxSearchHitsPerCall = 8;
    public const int DefaultSearchHits = 5;

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

    public async Task<InvestigationRecord> AskAsync(AskQuestion command, CancellationToken cancellationToken = default)
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
            snapshot, InvestigationType.Question, command.Question, command.IdempotencyKey);
        if (!await store.TryAddAsync(investigation, cancellationToken))
        {
            var winner = await store.FindByIdempotencyKeyAsync(
                command.RepositoryId, command.IdempotencyKey!, cancellationToken)
                ?? throw new InvalidOperationException("Idempotency key conflict without an existing investigation.");
            return await ReplayAsync(winner, command, cancellationToken);
        }

        var run = new Run(investigation, snapshot.CommitSha);
        investigation.Start(model.ModelId, QuestionPrompt.Version);
        await store.SaveAsync(investigation, [], cancellationToken);
        await EmitAsync(
            run, InvestigationEventType.Started,
            $"Started investigating at commit {snapshot.CommitSha[..12]}.", cancellationToken: cancellationToken);

        try
        {
            var (result, evidence) = await InvestigateAsync(run, command.Question, cancellationToken);

            await EmitAsync(
                run, InvestigationEventType.Completed,
                result.Outcome == InvestigationOutcome.Answered
                    ? $"Answered with {result.Evidence.Count} validated evidence reference(s)."
                    : "Reported that the evidence was insufficient to answer.",
                cancellationToken: cancellationToken);
            investigation.Complete(result.ToJson());
            await store.SaveAsync(investigation, evidence, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (investigation.State == InvestigationState.Running)
            {
                investigation.Cancel();
                await store.SaveAsync(investigation, [], CancellationToken.None);
            }

            throw;
        }
        catch (InvalidModelOutputException exception)
        {
            await FailAsync(run, "invalid_model_output", $"Failed: {exception.Message}");
        }
        catch (ChatModelException)
        {
            await FailAsync(run, "model_error", "Failed: the language model could not be reached.");
        }
        catch (Exception)
        {
            await FailAsync(run, "internal_error", "Failed unexpectedly.");
            throw;
        }

        return await store.GetAsync(investigation.Id, CancellationToken.None)
            ?? throw new InvalidOperationException("The investigation could not be read back.");
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
            var turn = await CompleteAsync(messages, cancellationToken);
            if (turn.ToolCalls.Count == 0)
            {
                finalTurn = turn;
                break;
            }

            messages.Add(new AssistantToolCallsMessage(turn.ToolCalls));
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
        try
        {
            // A snapshot of the conversation: the model must not be able to mutate our history.
            var request = new ModelRequest(QuestionPrompt.System, [.. messages], QuestionPrompt.Tools);
            return await model.CompleteAsync(request, cancellationToken)
                ?? throw new ChatModelException("The chat model returned no turn.");
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ChatModelException))
        {
            throw new ChatModelException("The chat model call failed.", exception);
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
                return ToolError(call, "budget_exhausted", "The tool output limit was reached. Answer with what you have.");
            }

            run.ToolOutputChars += content.Length;
            foreach (var (path, startLine, endLine) in seen)
            {
                run.MarkSeen(path, startLine, endLine);
            }

            await EmitAsync(
                run, InvestigationEventType.ToolCalled, summary, call.Name,
                JsonSerializer.Serialize(new { latencyMs = stopwatch.ElapsedMilliseconds, resultCount }, ToolResultJson),
                cancellationToken);
            return new ToolResultMessage(call.Id, call.Name, content, IsError: false);
        }
        catch (SourceToolException exception)
        {
            var code = JsonNamingPolicy.SnakeCaseLower.ConvertName(exception.Error.ToString());
            await EmitAsync(
                run, InvestigationEventType.ToolFailed,
                $"A {call.Name} call returned an error: {exception.Message}", call.Name,
                JsonSerializer.Serialize(new { latencyMs = stopwatch.ElapsedMilliseconds, error = code }, ToolResultJson),
                cancellationToken);
            return ToolError(call, code, exception.Message);
        }
    }

    private async Task<ToolOutput> SearchAsync(Run run, ToolCall call, CancellationToken cancellationToken)
    {
        var arguments = ParseArguments<SearchArguments>(call);
        var limit = Math.Clamp(arguments.Limit ?? DefaultSearchHits, 1, MaxSearchHitsPerCall);

        var result = await tools.SearchCodeAsync(
            run.Investigation.RepositoryId, run.Investigation.SnapshotId, arguments.Query!, limit, cancellationToken);

        var content = JsonSerializer.Serialize(
            new
            {
                result.CommitSha,
                hits = result.Hits.Select(hit => new
                {
                    hit.Path,
                    hit.StartLine,
                    hit.EndLine,
                    hit.Language,
                    hit.LexicalRank,
                    hit.VectorRank,
                    text = hit.Excerpt,
                }),
            },
            ToolResultJson);

        return new ToolOutput(
            content,
            $"Searched the code for \"{Clean(arguments.Query!, 120)}\": {result.Hits.Count} result(s).",
            result.Hits.Count,
            [.. result.Hits.Select(hit => (hit.Path, hit.StartLine, hit.EndLine))]);
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
            ToolResultJson);

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
        if (run.Investigation.State != InvestigationState.Running)
        {
            return;
        }

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
