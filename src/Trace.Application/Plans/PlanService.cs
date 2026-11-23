using System.Text;
using Trace.Application.Investigations;
using Trace.Application.Issues;
using Trace.Application.Telemetry;
using Trace.Domain;

namespace Trace.Application.Plans;

/// <summary>
/// A plan as a caller reads it: the stored plan, its investigation, and what both say once
/// parsed.
/// </summary>
/// <param name="Result">The plan document; null until the plan is finished.</param>
/// <param name="Summary">The run's account of the plan, which holds what validation removed; null until the plan is finished.</param>
public sealed record PlanView(
    Plan Plan,
    InvestigationRecord Investigation,
    string RepositoryOwner,
    string RepositoryName,
    PlanResult? Result,
    PlanRunSummary? Summary)
{
    public const string Planned = "planned";
    public const string InsufficientEvidence = "insufficient_evidence";

    /// <summary>
    /// One fixed name for where the plan stands: <c>queued</c> or <c>running</c> while it is
    /// unfinished, <c>planned</c> or <c>insufficient_evidence</c> once it has a document,
    /// <c>failed</c> or <c>cancelled</c> when it never will.
    /// </summary>
    public string State => Investigation.Investigation.State switch
    {
        InvestigationState.Completed when Result is { Outcome: PlanOutcome.Planned } => Planned,
        InvestigationState.Completed when Result is not null => InsufficientEvidence,

        // Completed without a document cannot be stored; never report an outcome that was not read.
        InvestigationState.Completed => "running",
        InvestigationState.Queued => "queued",
        InvestigationState.Running => "running",
        InvestigationState.Failed => "failed",
        _ => "cancelled",
    };
}

public enum PlanReviewOutcome
{
    /// <summary>No such plan, or the caller may not see its repository.</summary>
    NotFound,

    /// <summary>The plan has no steps to review: it is unfinished, failed, cancelled, or found insufficient evidence.</summary>
    NotReviewable,

    Marked,

    /// <summary>It was reviewed before; the first time stands.</summary>
    AlreadyReviewed,
}

public enum PlanExportOutcome
{
    NotFound,

    /// <summary>The plan has no document: it is unfinished, failed or cancelled.</summary>
    NotFinished,

    Exported,
}

public sealed record PlanExportFile(PlanExportOutcome Outcome, string? FileName = null, string? Markdown = null);

/// <summary>Whether the issue still reads as it did when the plan was made.</summary>
/// <param name="Status"><c>unchanged</c>, <c>changed</c> or <c>unavailable</c>.</param>
/// <param name="Code">A fixed name saying why it is unavailable.</param>
public sealed record PlanIssueCheck(string Status, string? Code = null)
{
    public const string Unchanged = "unchanged";
    public const string Changed = "changed";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Reading, reviewing and exporting stored plans for a caller. Every method starts from
/// <see cref="GetAsync"/>, which returns nothing for a repository the caller cannot see, so a
/// plan there is indistinguishable from one that does not exist.
/// <para>
/// Nothing here talks to a model. Only <see cref="CheckIssueAsync"/> reads from outside TRACE
/// (one issue, read-only, through <see cref="IssueReader"/>); reviewing writes one timestamp in
/// TRACE's own database and exporting writes nothing. Spans carry the plan's ID, counts and
/// fixed outcome names only: never the issue's title, the plan's text or source.
/// </para>
/// </summary>
public sealed class PlanService(
    IPlanStore plans,
    IInvestigationStore investigations,
    IssueReader issues,
    TimeProvider timeProvider)
{
    /// <exception cref="PlanSchemaException">The stored document is of a schema version this build cannot read.</exception>
    public async Task<PlanView?> GetAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        if (await plans.GetVisibleAsync(planId, cancellationToken) is not { } stored
            || await investigations.GetAsync(stored.Plan.InvestigationId, cancellationToken) is not { } record)
        {
            return null;
        }

        // The plan and its investigation are read by two queries, and a run can finish between
        // them: the plan read first is then still unfinished while the investigation already
        // says completed. Both are written in one transaction, so a plan read after a completed
        // investigation was seen has its document.
        if (record.Investigation.State == InvestigationState.Completed && !stored.Plan.IsFinished)
        {
            stored = await plans.GetVisibleAsync(planId, cancellationToken);
            if (stored is null)
            {
                return null;
            }
        }

        return View(stored, record);
    }

    /// <summary>A plan and its investigation as one view, with the stored documents parsed.</summary>
    public static PlanView View(StoredPlan stored, InvestigationRecord record)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(record);

        var finished = stored.Plan.ResultJson is not null && record.Investigation.ResultJson is not null;
        return new PlanView(
            stored.Plan,
            record,
            stored.RepositoryOwner,
            stored.RepositoryName,
            finished ? PlanResult.FromJson(stored.Plan.ResultJson!) : null,
            finished ? PlanRunSummary.FromJson(record.Investigation.ResultJson!) : null);
    }

    /// <summary>
    /// Records that a person has read the plan. Only a plan with steps (<c>planned</c>) can be
    /// reviewed: an unfinished, failed or cancelled plan has no document, and an
    /// insufficient-evidence result has no plan in it to acknowledge. Marking again changes
    /// nothing. Writes the reviewed time and nothing else, anywhere.
    /// </summary>
    public async Task<(PlanReviewOutcome Outcome, PlanView? Plan)> ReviewAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        using var activity = TraceTelemetry.Source.StartActivity("plan.review");
        activity?.SetTag("trace.plan_id", planId);

        var (outcome, view) = await ReviewCoreAsync(planId, cancellationToken);
        var name = outcome switch
        {
            PlanReviewOutcome.Marked => "marked",
            PlanReviewOutcome.AlreadyReviewed => "already_reviewed",
            PlanReviewOutcome.NotReviewable => "not_reviewable",
            _ => "not_found",
        };
        activity?.SetTag("trace.plan.review.outcome", name);
        if (view is not null)
        {
            activity?.SetTag("trace.plan.state", view.State);
        }

        return (outcome, view);
    }

    /// <summary>
    /// The plan as a Markdown document. A plan that found insufficient evidence has a document
    /// (it says so, and lists what is unknown) and is exported; a plan without one is not.
    /// </summary>
    public async Task<PlanExportFile> ExportAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        using var activity = TraceTelemetry.Source.StartActivity("plan.export");
        activity?.SetTag("trace.plan_id", planId);

        var view = await GetAsync(planId, cancellationToken);
        if (view?.Result is not { } result)
        {
            var missing = view is null ? PlanExportOutcome.NotFound : PlanExportOutcome.NotFinished;
            activity?.SetTag("trace.plan.export.outcome", view is null ? "not_found" : "not_finished");
            return new PlanExportFile(missing);
        }

        var investigation = view.Investigation.Investigation;
        var markdown = PlanMarkdown.Render(new PlanExport(
            view.Plan.Id,
            investigation.Id,
            view.RepositoryOwner,
            view.RepositoryName,
            view.Plan.IssueNumber,
            view.Plan.IssueTitle,
            view.Investigation.CommitSha,
            investigation.ModelId,
            investigation.PromptVersion,
            view.Plan.CreatedAt,
            view.Plan.ReviewedAt,
            result,
            view.Summary?.Plan.Trimmed));

        activity?.SetTag("trace.plan.export.outcome", "exported");
        activity?.SetTag("trace.plan.state", view.State);
        activity?.SetTag("trace.plan.steps", result.Steps.Count);
        activity?.SetTag("trace.plan.evidence", result.Evidence.Count);
        activity?.SetTag("trace.plan.export.bytes", Encoding.UTF8.GetByteCount(markdown));
        return new PlanExportFile(
            PlanExportOutcome.Exported, PlanMarkdown.FileName(view.Plan.Id, view.Plan.IssueNumber), markdown);
    }

    /// <summary>
    /// Reads the issue again and compares it with the fingerprint stored when the plan was made
    /// (<see cref="IssueContentHash"/>: title, body and the first comments, as cut by the
    /// limits). Read-only, and never an error: an issue that cannot be read is
    /// <c>unavailable</c> with a fixed code. Only call it with a plan from <see cref="GetAsync"/>;
    /// the issue reader checks the caller's access once more.
    /// </summary>
    public async Task<PlanIssueCheck> CheckIssueAsync(PlanView plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var activity = TraceTelemetry.Source.StartActivity("plan.issue_check");
        activity?.SetTag("trace.plan_id", plan.Plan.Id);

        var check = await CheckIssueCoreAsync(plan.Plan, cancellationToken);
        activity?.SetTag("trace.plan.issue_check.outcome", check.Code ?? check.Status);
        return check;
    }

    private async Task<(PlanReviewOutcome Outcome, PlanView? Plan)> ReviewCoreAsync(Guid planId, CancellationToken cancellationToken)
    {
        var view = await GetAsync(planId, cancellationToken);
        if (view is null)
        {
            return (PlanReviewOutcome.NotFound, null);
        }

        if (view.State != PlanView.Planned)
        {
            return (PlanReviewOutcome.NotReviewable, view);
        }

        var marked = await plans.MarkReviewedAsync(planId, timeProvider.GetUtcNow(), cancellationToken);
        view = await GetAsync(planId, cancellationToken);
        return view is null
            ? (PlanReviewOutcome.NotFound, null)
            : (marked ? PlanReviewOutcome.Marked : PlanReviewOutcome.AlreadyReviewed, view);
    }

    private async Task<PlanIssueCheck> CheckIssueCoreAsync(Plan plan, CancellationToken cancellationToken)
    {
        if (plan.IssueContentHash is not { } recorded)
        {
            // The run never read the issue (it is still queued, or failed before it could).
            return new PlanIssueCheck(PlanIssueCheck.Unavailable, "issue_not_recorded");
        }

        try
        {
            var issue = await issues.GetAsync(plan.RepositoryId, plan.IssueNumber, cancellationToken);
            return new PlanIssueCheck(
                IssueContentHash.Compute(issue) == recorded ? PlanIssueCheck.Unchanged : PlanIssueCheck.Changed);
        }
        catch (IssueReadException exception)
        {
            return new PlanIssueCheck(PlanIssueCheck.Unavailable, exception.Code);
        }
    }
}
