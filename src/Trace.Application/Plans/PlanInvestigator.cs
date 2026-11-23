using System.Globalization;
using System.Text.Json;
using Trace.Application.Investigations;
using Trace.Application.Issues;
using Trace.Application.Jobs;
using Trace.Application.Retrieval;
using Trace.Domain;

namespace Trace.Application.Plans;

public sealed record RequestPlan(Guid RepositoryId, Guid SnapshotId, int IssueNumber, string? IdempotencyKey = null);

/// <summary>A plan with the investigation that makes it.</summary>
public sealed record PlanRecord(Plan Plan, InvestigationRecord Investigation);

public interface IPlanStore
{
    /// <summary>
    /// Persists a new plan investigation, its unfinished plan and the job that will run it,
    /// atomically. Returns false, storing nothing, if the idempotency key is already taken.
    /// </summary>
    Task<bool> TryAddAsync(Investigation investigation, Plan plan, Job job, CancellationToken cancellationToken = default);

    /// <summary>The plan of an investigation, tracked for changes, regardless of repository visibility (workers only).</summary>
    Task<Plan?> FindForUpdateAsync(Guid investigationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the current state of the investigation and its plan together with the evidence,
    /// in one transaction. Finishing a plan needs exactly that: the database checks at commit
    /// that the investigation is completed and that every citation is one of these rows (ADR-016).
    /// </summary>
    Task SaveAsync(
        Investigation investigation,
        Plan plan,
        IReadOnlyList<EvidenceRef> evidence,
        CancellationToken cancellationToken = default);

    /// <summary>The plan of an investigation, only if its repository is visible to the caller.</summary>
    Task<Plan?> GetByInvestigationAsync(Guid investigationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Makes an implementation plan for one issue against one pinned snapshot.
/// <para>
/// It is the question workflow with a different prompt and a different ending: the same run,
/// the same bounded tool loop with the same two read-only tools and limits, the same citation
/// rule (<see cref="InvestigationRunner"/>). The server reads the issue once per attempt, shows
/// it to the model as quoted data (<see cref="PlanPrompt"/>), and keeps of the model's proposal
/// only what it can verify (<see cref="PlanAssembler"/>, ADR-017). The model cannot choose the
/// repository, the snapshot, the issue, a tool or a limit: none of them is read from anything
/// the model or the issue says.
/// </para>
/// </summary>
public sealed class PlanInvestigator(
    IRepositoryCatalog repositories,
    ISnapshotReader snapshots,
    IInvestigationStore store,
    IPlanStore plans,
    SourceTools tools,
    CitationValidator citationValidator,
    IssueReader issues,
    IChatModel model,
    ChatLimits limits,
    Access.ICurrentUser currentUser,
    TimeProvider timeProvider,
    IJobSignal jobSignal)
{
    private static readonly JsonSerializerOptions PayloadJson = JsonSerializerOptions.Web;

    private readonly InvestigationRunner runner = new(snapshots, store, tools, citationValidator, model, limits, timeProvider);

    /// <summary>
    /// What a plan investigation stores in the required <c>investigations.question</c>: a fixed
    /// sentence with the issue number. Never issue text.
    /// </summary>
    public static string QuestionFor(int issueNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"Plan the change requested in issue #{issueNumber}.");

    /// <summary>
    /// Validates the request and stores a queued plan investigation, its unfinished plan and the
    /// job that will run them, in one transaction. Nothing is fetched or investigated here: the
    /// issue is read when the job runs.
    /// </summary>
    public async Task<PlanRecord> CreateAsync(RequestPlan command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThan(command.IssueNumber, 1);

        if (!await repositories.IsVisibleAsync(command.RepositoryId, cancellationToken))
        {
            throw new InvestigationRequestException(
                InvestigationRequestError.RepositoryNotFound, "The repository does not exist.");
        }

        IndexSnapshot snapshot;
        try
        {
            snapshot = await SnapshotAccess.RequireCompletedAsync(
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

        if (command.IdempotencyKey is not null
            && await store.FindByIdempotencyKeyAsync(command.RepositoryId, command.IdempotencyKey, cancellationToken)
                is { } earlier)
        {
            return await ReplayAsync(earlier, command, cancellationToken);
        }

        var investigation = Investigation.Create(
            snapshot, InvestigationType.IssueToPlan, QuestionFor(command.IssueNumber), command.IdempotencyKey, currentUser.UserId);
        var plan = Plan.Create(investigation, command.IssueNumber);
        var job = Job.Enqueue(
            PlanJobHandler.JobKind,
            Job.RepositoryScope(investigation.RepositoryId),
            dedupeKey: investigation.Id.ToString(),
            PlanJobHandler.Payload(investigation.Id),
            timeProvider.GetUtcNow());

        if (!await plans.TryAddAsync(investigation, plan, job, cancellationToken))
        {
            var winner = await store.FindByIdempotencyKeyAsync(
                command.RepositoryId, command.IdempotencyKey!, cancellationToken)
                ?? throw new InvalidOperationException("Idempotency key conflict without an existing investigation.");
            return await ReplayAsync(winner, command, cancellationToken);
        }

        jobSignal.Notify();

        return await GetAsync(investigation.Id, cancellationToken)
            ?? throw new InvalidOperationException("The plan could not be read back.");
    }

    /// <summary>A plan and its investigation, only if the repository is visible to the caller.</summary>
    public async Task<PlanRecord?> GetAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
        await plans.GetByInvestigationAsync(investigationId, cancellationToken) is { } plan
        && await store.GetAsync(investigationId, cancellationToken) is { } record
            ? new PlanRecord(plan, record)
            : null;

    /// <summary>
    /// Runs a stored plan investigation to a final state. Safe to call again after an
    /// interrupted attempt: a finished investigation is left alone; an unfinished one starts
    /// over and reads the issue again. The plan gets its result, the investigation completes and
    /// the evidence rows are written in one transaction, so there is never half a plan and never
    /// a second one.
    /// </summary>
    public Task RunAsync(Guid investigationId, int attempt, int maxAttempts, CancellationToken cancellationToken) =>
        runner.RunAsync(
            investigationId, InvestigationType.IssueToPlan, PlanPrompt.Version, attempt, maxAttempts,
            (run, cancellation) => PlanAsync(run, attempt >= maxAttempts, cancellation),
            cancellationToken);

    /// <summary>
    /// Records that the job ended without finishing: the investigation becomes cancelled or
    /// failed, and its plan stays unfinished, still saying which issue it was about.
    /// </summary>
    public Task AbandonAsync(
        Guid investigationId,
        bool cancelled,
        string? jobErrorCode,
        CancellationToken cancellationToken = default) =>
        runner.AbandonAsync(investigationId, cancelled, jobErrorCode, cancellationToken);

    private async Task<PlanRecord> ReplayAsync(Investigation earlier, RequestPlan command, CancellationToken cancellationToken)
    {
        if (earlier.Type != InvestigationType.IssueToPlan
            || earlier.SnapshotId != command.SnapshotId
            || earlier.Question != QuestionFor(command.IssueNumber))
        {
            throw new InvestigationRequestException(
                InvestigationRequestError.IdempotencyConflict,
                "The idempotency key was already used for a different request.");
        }

        return await GetAsync(earlier.Id, cancellationToken)
            ?? throw new InvalidOperationException("The plan could not be read back.");
    }

    /// <summary>Makes the plan of a started investigation and returns a fixed name for how the run ended.</summary>
    private async Task<string> PlanAsync(InvestigationRun run, bool lastAttempt, CancellationToken cancellationToken)
    {
        var investigation = run.Investigation;
        var plan = await plans.FindForUpdateAsync(investigation.Id, cancellationToken)
            ?? throw new JobFailedException(JobErrors.InvalidPayload, "The plan investigation has no plan.");

        // The issue is read here, once per attempt, by the server: after the budget check and
        // before the first model call. The number comes from the stored plan.
        IssueDetail issue;
        try
        {
            issue = await issues.GetForRunAsync(investigation.RepositoryId, plan.IssueNumber, cancellationToken);
        }
        catch (IssueReadException exception) when (lastAttempt || !IsTransient(exception.Error))
        {
            await runner.FailAsync(run, exception.Code, IssueFailure(exception.Error));
            return exception.Code;
        }

        // What is recorded is what the model is about to be shown: the same object, the same limits.
        plan.RecordIssue(issue.Summary.Title, IssueContentHash.Compute(issue));
        await plans.SaveAsync(investigation, plan, [], cancellationToken);
        run.Activity?.SetTag("trace.issue.number", plan.IssueNumber);

        var (end, finalTurn) = await runner.ConverseAsync(
            run, PlanPrompt.System, PlanPrompt.Tools, PlanPrompt.FirstMessage(issue), cancellationToken);

        var accepted = new Dictionary<Citation, CitationCheck>();
        var assembled = end switch
        {
            ConversationEnd.TokenLimit => PlanAssembler.Insufficient(PlanAssembler.TokenLimitReached),
            ConversationEnd.StepLimit => PlanAssembler.Insufficient(PlanAssembler.StepLimitReached),
            _ => await ValidateAsync(run, ModelPlan.Parse(finalTurn!.FinalText), accepted, cancellationToken),
        };

        // One evidence row per location the kept entries cite; its ID is what the plan cites.
        var references = new List<EvidenceRef>();
        var evidence = new Dictionary<Citation, PlanEvidence>();
        foreach (var citation in assembled.CitedLocations())
        {
            var reference = InvestigationRunner.EvidenceFor(run, accepted[citation]);
            references.Add(reference);
            evidence[citation] = new PlanEvidence(
                reference.Id, citation.Path, citation.StartLine, citation.EndLine, accepted[citation].Excerpt!);
        }

        string planJson;
        try
        {
            planJson = assembled.ToResult(evidence).ToJson();
        }
        catch (PlanSchemaException)
        {
            // What validation kept still does not fit the stored document (the cited text is
            // too large, for example). Nothing of it is stored.
            throw new InvalidModelOutputException("The model's plan could not be stored within the limits of a plan.");
        }

        foreach (var removal in assembled.Removals)
        {
            await runner.EmitAsync(run, InvestigationEventType.CitationRejected, removal, cancellationToken: cancellationToken);
        }

        var planned = assembled.Outcome == PlanOutcome.Planned;
        await runner.EmitAsync(
            run, InvestigationEventType.Completed,
            planned
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Planned {assembled.Steps.Count} step(s) resting on {references.Count} validated evidence reference(s); removed {assembled.Trimmed.Items} unverified item(s).")
                : "Reported that the evidence was insufficient to plan the change.",
            metadataJson: JsonSerializer.Serialize(new { trimmed = assembled.Trimmed }, PayloadJson),
            cancellationToken: cancellationToken);

        investigation.Complete(PlanRunSummary.For(plan.Id, plan.IssueNumber, assembled, references.Count).ToJson());
        plan.Complete(investigation, PlanResult.CurrentSchemaVersion, planJson);
        await plans.SaveAsync(investigation, plan, references, cancellationToken);

        InvestigationRunner.TagCompleted(run, references.Count);
        run.Activity?.SetTag("trace.plan.steps", assembled.Steps.Count);
        run.Activity?.SetTag("trace.plan.removed_items", assembled.Trimmed.Items);
        run.Activity?.SetTag("trace.plan.discarded_citations", assembled.Trimmed.Citations);
        return planned ? "planned" : "insufficient_evidence";
    }

    private async Task<AssembledPlan> ValidateAsync(
        InvestigationRun run,
        ModelPlan proposal,
        Dictionary<Citation, CitationCheck> accepted,
        CancellationToken cancellationToken)
    {
        var claimed = proposal.CitedLocations();
        var checks = await runner.ValidateCitationsAsync(run, claimed, cancellationToken);
        for (var index = 0; index < claimed.Count; index++)
        {
            if (checks[index] is { } check)
            {
                accepted[claimed[index]] = check;
            }
        }

        // Whether each proposed path is a file of the pinned snapshot: asked of the index, never
        // of a file system, and only for paths that are clean.
        var inSnapshot = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var path in (proposal.FilesToChange ?? []).Select(file => file.Path))
        {
            if (PlanPaths.IsClean(path) && !inSnapshot.ContainsKey(path!))
            {
                inSnapshot[path!] = await snapshots.FindLastLineAsync(run.Investigation.SnapshotId, path!, cancellationToken) is not null;
            }
        }

        return PlanAssembler.Assemble(proposal, accepted.Keys.ToHashSet(), inSnapshot);
    }

    /// <summary>GitHub may answer the next attempt; everything else will be the same again.</summary>
    private static bool IsTransient(IssueReadError error) =>
        error is IssueReadError.GitHubRateLimited or IssueReadError.GitHubUnavailable;

    private static string IssueFailure(IssueReadError error) => error switch
    {
        IssueReadError.IssueNotFound => "Failed: the repository has no issue with that number.",
        IssueReadError.IssuesPermissionMissing => "Failed: the GitHub App installation has not granted read access to issues.",
        IssueReadError.IssuesDisabled => "Failed: issues are switched off for this repository on GitHub.",
        IssueReadError.IssuesUnavailable or IssueReadError.RepositoryNotFound => "Failed: this repository has no source of issues.",
        IssueReadError.GitHubRateLimited => "Failed: GitHub was limiting requests and the issue could not be read.",
        _ => "Failed: GitHub did not return the issue.",
    };
}

/// <summary>Runs a queued plan investigation on a worker.</summary>
public sealed class PlanJobHandler(PlanInvestigator investigator) : IJobHandler
{
    public const string JobKind = "plan.run";

    public string Kind => JobKind;

    public static string Payload(Guid investigationId) =>
        JsonSerializer.Serialize(new PlanJobPayload(investigationId), JsonSerializerOptions.Web);

    public Task HandleAsync(JobContext context) =>
        investigator.RunAsync(
            InvestigationId(context.PayloadJson), context.Attempt, context.MaxAttempts, context.CancellationToken);

    public Task OnAbandonedAsync(
        Guid jobId,
        string payloadJson,
        JobStatus finalStatus,
        string? errorCode,
        CancellationToken cancellationToken) =>
        investigator.AbandonAsync(
            InvestigationId(payloadJson), cancelled: finalStatus == JobStatus.Cancelled, errorCode, cancellationToken);

    private static Guid InvestigationId(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<PlanJobPayload>(payloadJson, JsonSerializerOptions.Web)!.InvestigationId;
        }
        catch (Exception exception) when (exception is JsonException or NullReferenceException)
        {
            throw new JobFailedException(JobErrors.InvalidPayload, "The job payload is not a valid investigation reference.", exception);
        }
    }

    private sealed record PlanJobPayload(Guid InvestigationId);
}
