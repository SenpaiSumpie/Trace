using Trace.Application.Issues;
using Trace.Application.Plans;
using Trace.Domain;

namespace Trace.UnitTests;

/// <summary>
/// The rules of a stored plan: whose it is, when it is finished, and that reviewing a finished
/// plan is the only thing that can still happen to it. The database enforces the same rules
/// (<c>PlanStorageTests</c>).
/// </summary>
public class PlanRuleTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly DateTimeOffset Noon = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_plan_takes_its_snapshot_and_repository_from_its_investigation_and_starts_unfinished()
    {
        var investigation = NewInvestigation();

        var plan = Plan.Create(investigation, issueNumber: 42);

        Assert.Equal(investigation.Id, plan.InvestigationId);
        Assert.Equal(investigation.SnapshotId, plan.SnapshotId);
        Assert.Equal(investigation.RepositoryId, plan.RepositoryId);
        Assert.Equal(42, plan.IssueNumber);
        Assert.False(plan.IsFinished);
        Assert.False(plan.IsReviewed);
        Assert.Null(plan.IssueTitle);
        Assert.Null(plan.IssueContentHash);
        Assert.Null(plan.SchemaVersion);
        Assert.Null(plan.ResultJson);
        Assert.Null(plan.ReviewedAt);
    }

    [Fact]
    public void A_question_investigation_has_no_plan()
    {
        var question = NewInvestigation(InvestigationType.Question);

        Assert.Throws<InvalidOperationException>(() => Plan.Create(question, 42));
    }

    [Fact]
    public void A_plan_cannot_be_added_to_an_investigation_that_already_ended()
    {
        var completed = NewInvestigation();
        completed.Start("model", "prompt");
        completed.Complete("{}");
        var failed = NewInvestigation();
        failed.Fail("model_error");
        var cancelled = NewInvestigation();
        cancelled.Cancel();

        Assert.Throws<InvalidOperationException>(() => Plan.Create(completed, 42));
        Assert.Throws<InvalidOperationException>(() => Plan.Create(failed, 42));
        Assert.Throws<InvalidOperationException>(() => Plan.Create(cancelled, 42));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void An_issue_number_starts_at_one(int issueNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan.Create(NewInvestigation(), issueNumber));
    }

    [Fact]
    public void The_issue_can_be_recorded_again_until_the_plan_is_finished()
    {
        var investigation = NewInvestigation();
        var plan = Plan.Create(investigation, 42);

        plan.RecordIssue("First reading", Hash);
        plan.RecordIssue(string.Empty, new string('b', 64));

        Assert.Equal(string.Empty, plan.IssueTitle);
        Assert.Equal(new string('b', 64), plan.IssueContentHash);

        Finish(investigation, plan);
        Assert.Throws<InvalidOperationException>(() => plan.RecordIssue("Edited later", Hash));
        Assert.Equal(string.Empty, plan.IssueTitle);
    }

    [Fact]
    public void An_issue_title_is_one_line_of_at_most_the_length_issues_are_cut_to()
    {
        var plan = Plan.Create(NewInvestigation(), 42);

        Assert.Equal(IssueLimits.MaxTitleLength, Plan.MaxIssueTitleLength);
        plan.RecordIssue(new string('x', Plan.MaxIssueTitleLength), Hash);
        Assert.Throws<ArgumentException>(() => plan.RecordIssue(new string('x', Plan.MaxIssueTitleLength + 1), Hash));
        Assert.Throws<ArgumentException>(() => plan.RecordIssue("two\nlines", Hash));
        Assert.Throws<ArgumentException>(() => plan.RecordIssue("nul\0inside", Hash));
        Assert.Throws<ArgumentNullException>(() => plan.RecordIssue(null!, Hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void An_issue_hash_is_a_lowercase_hex_sha256(string hash)
    {
        var plan = Plan.Create(NewInvestigation(), 42);

        Assert.Throws<ArgumentException>(() => plan.RecordIssue("Title", hash));
        Assert.Null(plan.IssueContentHash);
    }

    [Fact]
    public void A_plan_is_finished_by_its_completed_investigation_once()
    {
        var investigation = NewInvestigation();
        var plan = Plan.Create(investigation, 42);
        plan.RecordIssue("Cap the page size", Hash);
        investigation.Start("model", "prompt");
        investigation.Complete("{}");

        plan.Complete(investigation, PlanResult.CurrentSchemaVersion, """{"schemaVersion":"plan-v1"}""");

        Assert.True(plan.IsFinished);
        Assert.Equal(PlanResult.CurrentSchemaVersion, plan.SchemaVersion);
        Assert.Equal("""{"schemaVersion":"plan-v1"}""", plan.ResultJson);
        Assert.Throws<InvalidOperationException>(
            () => plan.Complete(investigation, PlanResult.CurrentSchemaVersion, """{"schemaVersion":"plan-v1","other":1}"""));
        Assert.Equal("""{"schemaVersion":"plan-v1"}""", plan.ResultJson);
    }

    [Fact]
    public void A_plan_cannot_be_finished_while_its_investigation_has_not_completed()
    {
        var queued = NewInvestigation();
        var running = NewInvestigation();
        var failed = NewInvestigation();
        var cancelled = NewInvestigation();
        var plans = new[] { queued, running, failed, cancelled }
            .Select(investigation =>
            {
                var plan = Plan.Create(investigation, 42);
                plan.RecordIssue("Title", Hash);
                return (investigation, plan);
            })
            .ToList();
        running.Start("model", "prompt");
        failed.Fail("model_error");
        cancelled.Cancel();

        Assert.All(plans, pair =>
        {
            Assert.Throws<InvalidOperationException>(
                () => pair.plan.Complete(pair.investigation, PlanResult.CurrentSchemaVersion, "{}"));
            Assert.False(pair.plan.IsFinished);
        });
    }

    [Fact]
    public void A_plan_cannot_be_finished_by_another_investigation()
    {
        var plan = Plan.Create(NewInvestigation(), 42);
        plan.RecordIssue("Title", Hash);
        var other = NewInvestigation();
        other.Start("model", "prompt");
        other.Complete("{}");

        Assert.Throws<InvalidOperationException>(() => plan.Complete(other, PlanResult.CurrentSchemaVersion, "{}"));
    }

    [Fact]
    public void A_plan_cannot_be_finished_before_its_issue_was_recorded()
    {
        var investigation = NewInvestigation();
        var plan = Plan.Create(investigation, 42);
        investigation.Start("model", "prompt");
        investigation.Complete("{}");

        Assert.Throws<InvalidOperationException>(() => plan.Complete(investigation, PlanResult.CurrentSchemaVersion, "{}"));
    }

    [Theory]
    [InlineData("", "{}")]
    [InlineData("plan-v1", "")]
    [InlineData("plan-v1", "  ")]
    [InlineData("a-version-name-longer-than-twenty", "{}")]
    public void A_finished_plan_needs_a_schema_version_and_a_result(string schemaVersion, string resultJson)
    {
        var investigation = NewInvestigation();
        var plan = Plan.Create(investigation, 42);
        plan.RecordIssue("Title", Hash);
        investigation.Start("model", "prompt");
        investigation.Complete("{}");

        Assert.Throws<ArgumentException>(() => plan.Complete(investigation, schemaVersion, resultJson));
        Assert.False(plan.IsFinished);
    }

    [Fact]
    public void Marking_a_finished_plan_reviewed_records_the_time_in_utc_and_nothing_else()
    {
        var (_, plan) = FinishedPlan();
        var result = plan.ResultJson;

        var marked = plan.MarkReviewed(new DateTimeOffset(2026, 11, 23, 7, 0, 0, TimeSpan.FromHours(-5)));

        Assert.True(marked);
        Assert.True(plan.IsReviewed);
        Assert.Equal(Noon, plan.ReviewedAt);
        Assert.Equal(TimeSpan.Zero, plan.ReviewedAt!.Value.Offset);
        Assert.Equal(result, plan.ResultJson);
        Assert.Equal("Cap the page size", plan.IssueTitle);
        Assert.Equal(Hash, plan.IssueContentHash);
        Assert.Equal(42, plan.IssueNumber);
    }

    [Fact]
    public void Marking_reviewed_twice_keeps_the_first_time_and_is_not_an_error()
    {
        var (_, plan) = FinishedPlan();

        var first = plan.MarkReviewed(Noon);
        var second = plan.MarkReviewed(Noon.AddDays(3));

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(Noon, plan.ReviewedAt);
    }

    [Fact]
    public void An_unfinished_plan_cannot_be_reviewed_whatever_state_its_investigation_is_in()
    {
        var queued = NewInvestigation();
        var running = NewInvestigation();
        var failed = NewInvestigation();
        var cancelled = NewInvestigation();

        // Completed, but the plan was never given its result.
        var completed = NewInvestigation();
        var plans = new[] { queued, running, failed, cancelled, completed }
            .Select(investigation => Plan.Create(investigation, 42))
            .ToList();
        running.Start("model", "prompt");
        failed.Fail("model_error");
        cancelled.Cancel();
        completed.Start("model", "prompt");
        completed.Complete("{}");

        Assert.All(plans, plan =>
        {
            Assert.Throws<InvalidOperationException>(() => plan.MarkReviewed(Noon));
            Assert.Null(plan.ReviewedAt);
        });
    }

    private static (Investigation Investigation, Plan Plan) FinishedPlan()
    {
        var investigation = NewInvestigation();
        var plan = Plan.Create(investigation, 42);
        Finish(investigation, plan, "Cap the page size");
        return (investigation, plan);
    }

    private static void Finish(Investigation investigation, Plan plan, string? title = null)
    {
        if (title is not null)
        {
            plan.RecordIssue(title, Hash);
        }

        investigation.Start("model", "prompt");
        investigation.Complete("{}");
        plan.Complete(investigation, PlanResult.CurrentSchemaVersion, """{"schemaVersion":"plan-v1"}""");
    }

    private static Investigation NewInvestigation(InvestigationType type = InvestigationType.IssueToPlan)
    {
        var snapshot = IndexSnapshot.Start(Guid.NewGuid(), new string('a', 40), "main", "p", "m");
        snapshot.Complete(DateTimeOffset.UtcNow);
        return Investigation.Create(snapshot, type, "Plan issue 42");
    }
}
