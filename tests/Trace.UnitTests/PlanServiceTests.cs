using Trace.Application.Investigations;
using Trace.Application.Issues;
using Trace.Application.Plans;
using Trace.Domain;

namespace Trace.UnitTests;

/// <summary>
/// Reading and reviewing plans for a caller, with the stores replaced by ones a test controls.
/// What the database enforces is tested against PostgreSQL in the integration tests.
/// </summary>
public class PlanServiceTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly Guid EvidenceId = Guid.Parse("01938a00-0000-7000-8000-0000000000e1");

    [Fact]
    public async Task A_run_that_finishes_between_the_two_reads_is_shown_with_its_document_not_with_an_outcome_nobody_read()
    {
        // The plan is read while unfinished; by the time its investigation is read, the run has
        // completed both. The plan is read again rather than reported from half of each.
        var investigation = NewInvestigation();
        var unfinished = Plan.Create(investigation, 42);
        var finished = Plan.Create(investigation, 42);
        Finish(investigation, finished, PlanOutcome.Planned);
        var plans = new StubPlans(unfinished, finished);
        var service = Service(plans, investigation);

        var view = await service.GetAsync(unfinished.Id);

        Assert.Equal(2, plans.Reads);
        Assert.Same(finished, view!.Plan);
        Assert.Equal("planned", view.State);
        Assert.Single(view.Result!.Steps);
        Assert.Equal(PlanTrimmed.Nothing with { Steps = 2 }, view.Summary!.Plan.Trimmed);
    }

    [Theory]
    [InlineData(InvestigationState.Queued, "queued")]
    [InlineData(InvestigationState.Running, "running")]
    [InlineData(InvestigationState.Failed, "failed")]
    [InlineData(InvestigationState.Cancelled, "cancelled")]
    public async Task A_plan_without_a_document_reports_where_its_investigation_stands(InvestigationState state, string expected)
    {
        var investigation = NewInvestigation();
        var plan = Plan.Create(investigation, 42);
        switch (state)
        {
            case InvestigationState.Running:
                investigation.Start("model", "prompt");
                break;
            case InvestigationState.Failed:
                investigation.Start("model", "prompt");
                investigation.Fail("issue_not_found");
                break;
            case InvestigationState.Cancelled:
                investigation.Cancel();
                break;
        }

        var plans = new StubPlans(plan);
        var service = Service(plans, investigation);

        var view = await service.GetAsync(plan.Id);
        var (review, _) = await service.ReviewAsync(plan.Id);
        var export = await service.ExportAsync(plan.Id);

        Assert.Equal(state, investigation.State);
        Assert.Equal(expected, view!.State);
        Assert.Null(view.Result);
        Assert.Null(view.Summary);
        Assert.Equal(PlanReviewOutcome.NotReviewable, review);
        Assert.Equal(PlanExportOutcome.NotFinished, export.Outcome);
        Assert.Null(export.Markdown);
        Assert.Equal(0, plans.Marks);
    }

    [Fact]
    public async Task An_insufficient_evidence_result_is_exported_but_is_not_something_to_review()
    {
        var investigation = NewInvestigation();
        var plan = Plan.Create(investigation, 42);
        Finish(investigation, plan, PlanOutcome.InsufficientEvidence);
        var plans = new StubPlans(plan);
        var service = Service(plans, investigation);

        var (review, view) = await service.ReviewAsync(plan.Id);
        var export = await service.ExportAsync(plan.Id);

        Assert.Equal(PlanReviewOutcome.NotReviewable, review);
        Assert.Equal("insufficient_evidence", view!.State);
        Assert.Equal(0, plans.Marks);
        Assert.Equal(PlanExportOutcome.Exported, export.Outcome);
        Assert.Contains("- Outcome: insufficient evidence.", export.Markdown, StringComparison.Ordinal);
        Assert.Equal($"trace-plan-issue-42-{plan.Id:N}.md", export.FileName);
    }

    [Fact]
    public async Task Reviewing_marks_a_planned_plan_once_and_a_plan_the_caller_cannot_see_does_not_exist()
    {
        var investigation = NewInvestigation();
        var plan = Plan.Create(investigation, 42);
        Finish(investigation, plan, PlanOutcome.Planned);
        var plans = new StubPlans(plan);
        var service = Service(plans, investigation);

        var (first, marked) = await service.ReviewAsync(plan.Id);
        var (second, again) = await service.ReviewAsync(plan.Id);

        Assert.Equal((PlanReviewOutcome.Marked, PlanReviewOutcome.AlreadyReviewed), (first, second));
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero), marked!.Plan.ReviewedAt);
        Assert.Equal(marked.Plan.ReviewedAt, again!.Plan.ReviewedAt);
        Assert.Equal(2, plans.Marks);

        plans.Visible = false;
        var (hidden, nothing) = await service.ReviewAsync(plan.Id);
        Assert.Equal(PlanReviewOutcome.NotFound, hidden);
        Assert.Null(nothing);
        Assert.Null(await service.GetAsync(plan.Id));
        Assert.Equal(PlanExportOutcome.NotFound, (await service.ExportAsync(plan.Id)).Outcome);
        Assert.Equal(2, plans.Marks);
    }

    private static PlanService Service(StubPlans plans, Investigation investigation) =>
        new(plans, new StubInvestigations(investigation), new IssueReader(new NoRepositories(), new NoIssues()), new FixedTime());

    private static void Finish(Investigation investigation, Plan plan, PlanOutcome outcome)
    {
        var planned = outcome == PlanOutcome.Planned;
        var result = new PlanResult(
            PlanResult.CurrentSchemaVersion,
            outcome,
            new PlanStatement("A summary.", planned ? [EvidenceId] : []),
            Steps: planned ? [new PlanStatement("A step.", [EvidenceId])] : [],
            FilesToChange: [],
            Risks: [],
            Verification: [],
            Unknowns: [new PlanStatement("An unknown.", [])],
            Evidence: planned ? [new PlanEvidence(EvidenceId, "src/a.cs", 1, 2, "line one\nline two")] : []);
        var summary = new PlanRunSummary(
            InvestigationResult.CurrentSchemaVersion,
            planned ? InvestigationOutcome.Answered : InvestigationOutcome.InsufficientEvidence,
            "A fixed sentence.",
            Evidence: [],
            Unknowns: [],
            Limitations: [],
            new PlanRunDetails(plan.Id, plan.IssueNumber, outcome, result.Steps.Count, result.Evidence.Count, PlanTrimmed.Nothing with { Steps = 2 }));

        plan.RecordIssue("Cap the page size", Hash);
        if (investigation.State == InvestigationState.Queued)
        {
            investigation.Start("model", "prompt");
            investigation.Complete(summary.ToJson());
        }

        plan.Complete(investigation, PlanResult.CurrentSchemaVersion, result.ToJson());
    }

    private static Investigation NewInvestigation()
    {
        var snapshot = IndexSnapshot.Start(Guid.NewGuid(), new string('a', 40), "main", "p", "m");
        snapshot.Complete(DateTimeOffset.UtcNow);
        return Investigation.Create(snapshot, InvestigationType.IssueToPlan, "Plan the change requested in issue #42.");
    }

    /// <summary>Serves the given plans in order, one per read; the last one repeats.</summary>
    private sealed class StubPlans(params Plan[] reads) : IPlanStore
    {
        public int Reads { get; private set; }

        public int Marks { get; private set; }

        public bool Visible { get; set; } = true;

        public Task<StoredPlan?> GetVisibleAsync(Guid planId, CancellationToken cancellationToken = default)
        {
            var plan = reads[Math.Min(Reads++, reads.Length - 1)];
            return Task.FromResult(Visible ? new StoredPlan(plan, "owner", "name") : null);
        }

        public Task<bool> MarkReviewedAsync(Guid planId, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Marks++;
            return Task.FromResult(Visible && reads[^1].MarkReviewed(now));
        }

        public Task<bool> TryAddAsync(Investigation investigation, Plan plan, Job job, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Plan?> FindForUpdateAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveAsync(Investigation investigation, Plan plan, IReadOnlyList<EvidenceRef> evidence, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Plan?> GetByInvestigationAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubInvestigations(Investigation investigation) : IInvestigationStore
    {
        public Task<InvestigationRecord?> GetAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<InvestigationRecord?>(
                investigationId == investigation.Id ? new InvestigationRecord(investigation, new string('a', 40), [], JobId: null) : null);

        public Task<Investigation?> FindByIdempotencyKeyAsync(Guid repositoryId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryAddAsync(Investigation added, Job job, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Investigation?> FindForUpdateAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetLastEventSeqAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AppendEventAsync(InvestigationEvent investigationEvent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveAsync(Investigation saved, IReadOnlyList<EvidenceRef> evidence, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> SumTokensSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Reviewing and exporting never ask about repositories or issues: if they did, these would throw.</summary>
    private sealed class NoRepositories : IRepositoryCatalog
    {
        public Task<IReadOnlyList<RepositorySummary>> ListVisibleAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsVisibleAsync(Guid repositoryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoIssues : IIssueSourceResolver
    {
        public Task<IIssueSource?> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    }
}
