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
    IChatModel model,
    ChatLimits limits,
    Access.ICurrentUser currentUser,
    TimeProvider timeProvider,
    Jobs.IJobSignal jobSignal)
{
    public const int MaxModelTurns = InvestigationRunner.MaxModelTurns;
    public const int MaxToolCalls = InvestigationRunner.MaxToolCalls;
    public const int MaxSearchHitsPerCall = InvestigationRunner.MaxSearchHitsPerCall;
    public const int DefaultSearchHits = InvestigationRunner.DefaultSearchHits;

    /// <summary>
    /// How many hits of one search are returned with their full text. Further hits come back as
    /// a one-line preview the model can open with read_file_range, so a wide search does not
    /// cost a full passage for every weak match.
    /// </summary>
    public const int FullTextHitsPerSearch = InvestigationRunner.FullTextHitsPerSearch;

    public const int MaxPreviewLength = InvestigationRunner.MaxPreviewLength;

    public const string SearchNote = InvestigationRunner.SearchNote;

    /// <summary>Upper bound on the total characters of tool output given to the model in one investigation.</summary>
    public const int MaxToolOutputChars = InvestigationRunner.MaxToolOutputChars;

    public const string InsufficientEvidenceAnswer =
        "TRACE could not find enough evidence in the indexed snapshot to answer this question.";

    /// <summary>The run, the tool loop and the citation rule, shared with plan investigations.</summary>
    private readonly InvestigationRunner runner = new(snapshots, store, tools, citationValidator, model, limits, timeProvider);

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
    public Task RunAsync(Guid investigationId, int attempt, int maxAttempts, CancellationToken cancellationToken) =>
        runner.RunAsync(
            investigationId, InvestigationType.Question, QuestionPrompt.Version, attempt, maxAttempts,
            AnswerAsync, cancellationToken);

    /// <summary>
    /// Records that an investigation's job ended without finishing it: cancelled on request, or
    /// failed for good. Does nothing if the investigation already has a final state.
    /// </summary>
    public Task AbandonAsync(
        Guid investigationId,
        bool cancelled,
        string? jobErrorCode,
        CancellationToken cancellationToken = default) =>
        runner.AbandonAsync(investigationId, cancelled, jobErrorCode, cancellationToken);

    /// <summary>Answers the started investigation and returns a fixed name for how it ended.</summary>
    private async Task<string> AnswerAsync(InvestigationRun run, CancellationToken cancellationToken)
    {
        var investigation = run.Investigation;
        var (result, evidence) = await InvestigateAsync(run, investigation.Question, cancellationToken);

        await runner.EmitAsync(
            run, InvestigationEventType.Completed,
            result.Outcome == InvestigationOutcome.Answered
                ? $"Answered with {result.Evidence.Count} validated evidence reference(s)."
                : "Reported that the evidence was insufficient to answer.",
            cancellationToken: cancellationToken);
        investigation.Complete(result.ToJson());
        await store.SaveAsync(investigation, evidence, cancellationToken);
        InvestigationRunner.TagCompleted(run, result.Evidence.Count);
        return result.Outcome == InvestigationOutcome.Answered ? "answered" : "insufficient_evidence";
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
        if (earlier.Type != InvestigationType.Question
            || earlier.SnapshotId != command.SnapshotId
            || earlier.Question != command.Question)
        {
            throw new InvestigationRequestException(
                InvestigationRequestError.IdempotencyConflict,
                "The idempotency key was already used for a different request.");
        }

        return await store.GetAsync(earlier.Id, cancellationToken)
            ?? throw new InvalidOperationException("The investigation could not be read back.");
    }

    private async Task<(InvestigationResult Result, IReadOnlyList<EvidenceRef> Evidence)> InvestigateAsync(
        InvestigationRun run,
        string question,
        CancellationToken cancellationToken)
    {
        var (end, finalTurn) = await runner.ConverseAsync(
            run, QuestionPrompt.System, QuestionPrompt.Tools, question, cancellationToken);
        if (end == ConversationEnd.TokenLimit)
        {
            return (Insufficient([], ["The investigation reached its token limit before the model produced an answer."]), []);
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

    private async Task<(List<EvidenceItem> Evidence, List<EvidenceRef> References)> ValidateCitationsAsync(
        InvestigationRun run,
        ModelAnswer answer,
        CancellationToken cancellationToken)
    {
        var claimed = (answer.Citations ?? [])
            .DistinctBy(citation => (citation.Path, citation.StartLine, citation.EndLine))
            .ToList();
        var accepted = await runner.ValidateCitationsAsync(
            run,
            [.. claimed.Select(citation => new Citation(citation.Path ?? string.Empty, citation.StartLine, citation.EndLine))],
            cancellationToken);

        var evidence = new List<EvidenceItem>();
        var references = new List<EvidenceRef>();
        for (var index = 0; index < claimed.Count; index++)
        {
            if (accepted[index] is not { } check)
            {
                continue;
            }

            var citation = check.Citation;
            evidence.Add(new EvidenceItem(
                citation.Path, citation.StartLine, citation.EndLine,
                Clean(claimed[index].Supports ?? string.Empty, ModelAnswer.MaxShortTextLength),
                check.Excerpt!));
            references.Add(InvestigationRunner.EvidenceFor(run, check));
        }

        return (evidence, references);
    }

    private static InvestigationResult Insufficient(IReadOnlyList<string> unknowns, IReadOnlyList<string> limitations) =>
        new(
            InvestigationResult.CurrentSchemaVersion,
            InvestigationOutcome.InsufficientEvidence,
            InsufficientEvidenceAnswer,
            Evidence: [],
            unknowns,
            limitations);

    private static string Clean(string? text, int maxLength) => InvestigationRunner.Clean(text, maxLength);
}
