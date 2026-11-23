using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trace.Application.Plans;
using Trace.Domain;
using Trace.Infrastructure.Persistence;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>
/// What must always hold for a stored plan, tried against PostgreSQL itself with raw SQL that
/// bypasses the domain model (ADR-016): a plan belongs to exactly one investigation, its
/// citations are evidence of that investigation and so lie in its snapshot, and once a plan is
/// finished nothing but its review can be recorded.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PlanStorageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string ForeignKeyViolation = PostgresErrorCodes.ForeignKeyViolation;
    private const string CheckViolation = PostgresErrorCodes.CheckViolation;
    private const string UniqueViolation = PostgresErrorCodes.UniqueViolation;

    private const string Version = PlanResult.CurrentSchemaVersion;

    private static readonly string Hash = new('a', 64);

    private TestDatabase database = null!;
    private TraceDbContext db = null!;

    public async Task InitializeAsync()
    {
        database = await postgres.CreateDatabaseAsync();
        db = database.CreateContext();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await database.DisposeAsync();
    }

    // ---- A plan belongs to exactly one investigation ----

    [Fact]
    public async Task Plan_requires_an_existing_investigation()
    {
        var scenario = await AddUnfinishedPlanAsync();

        await AssertRejectedAsync(
            () => InsertPlanAsync(Guid.NewGuid(), scenario.Snapshot.Id, scenario.Repository.Id),
            ForeignKeyViolation,
            "fk_plans_investigations");
    }

    [Fact]
    public async Task An_investigation_has_at_most_one_plan()
    {
        var scenario = await AddUnfinishedPlanAsync();

        await AssertRejectedAsync(
            () => InsertPlanAsync(scenario.Investigation.Id, scenario.Snapshot.Id, scenario.Repository.Id, issueNumber: 43),
            UniqueViolation,
            "ux_plans_investigation");
    }

    [Fact]
    public async Task Plan_cannot_name_another_snapshot_than_its_investigations()
    {
        var repository = await TestGraph.AddRepositoryAsync(db);
        var (pinned, _) = await TestGraph.AddCompletedSnapshotAsync(db, repository, TestGraph.ShaA);
        var (other, _) = await TestGraph.AddCompletedSnapshotAsync(db, repository, TestGraph.ShaB);
        var investigation = await TestGraph.AddInvestigationAsync(db, pinned);

        await AssertRejectedAsync(
            () => InsertPlanAsync(investigation.Id, other.Id, repository.Id),
            ForeignKeyViolation,
            "fk_plans_investigations");
    }

    [Fact]
    public async Task Plan_cannot_name_another_repository_than_its_investigations()
    {
        var (snapshot, _) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));
        var otherRepository = await TestGraph.AddRepositoryAsync(db, "other-repo");
        var investigation = await TestGraph.AddInvestigationAsync(db, snapshot);

        await AssertRejectedAsync(
            () => InsertPlanAsync(investigation.Id, snapshot.Id, otherRepository.Id),
            ForeignKeyViolation,
            "fk_plans_investigations");
    }

    [Fact]
    public async Task A_question_investigation_cannot_have_a_plan()
    {
        var (snapshot, _) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));
        var question = Investigation.Create(snapshot, InvestigationType.Question, "Where is paging handled?");
        db.Add(question);
        await db.SaveChangesAsync();

        await AssertRejectedAsync(
            () => InsertPlanAsync(question.Id, snapshot.Id, snapshot.RepositoryId),
            CheckViolation,
            "plans_investigation_type");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_cannot_be_moved_to_another_investigation_or_issue(bool finished)
    {
        var scenario = finished ? await AddFinishedPlanAsync() : await AddUnfinishedPlanAsync();
        var other = await TestGraph.AddInvestigationAsync(db, scenario.Snapshot);
        var planId = scenario.Plan.Id;

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"UPDATE plans SET investigation_id = {other.Id} WHERE id = {planId}"),
            CheckViolation,
            "plans_identity_immutable");
        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"UPDATE plans SET issue_number = 43 WHERE id = {planId}"),
            CheckViolation,
            "plans_identity_immutable");
        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"UPDATE plans SET created_at = created_at - interval '1 day' WHERE id = {planId}"),
            CheckViolation,
            "plans_identity_immutable");
    }

    [Fact]
    public async Task Deleting_an_investigation_removes_its_finished_plan_and_evidence()
    {
        var scenario = await AddFinishedPlanAsync();

        await db.Database.ExecuteSqlAsync($"DELETE FROM investigations WHERE id = {scenario.Investigation.Id}");

        Assert.Equal(0, await db.Plans.CountAsync());
        Assert.Equal(0, await db.EvidenceRefs.CountAsync());
    }

    [Fact]
    public async Task Deleting_a_repository_removes_its_plans()
    {
        var scenario = await AddFinishedPlanAsync();
        await db.Database.ExecuteSqlAsync($"UPDATE plans SET reviewed_at = now() WHERE id = {scenario.Plan.Id}");

        await db.Database.ExecuteSqlAsync($"DELETE FROM repositories WHERE id = {scenario.Repository.Id}");

        Assert.Equal(0, await db.Plans.CountAsync());
        Assert.Equal(0, await db.Investigations.CountAsync());
        Assert.Equal(0, await db.EvidenceRefs.CountAsync());
    }

    // ---- A plan is finished only by a completed investigation ----

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task Plan_cannot_get_a_result_while_its_investigation_has_not_completed(string state)
    {
        var scenario = await AddUnfinishedPlanAsync();
        await MoveInvestigationAsync(scenario.Investigation, state);

        await AssertRejectedAsync(
            () => FinishWithSqlAsync(scenario.Plan.Id, Version, $$"""{"schemaVersion":"{{Version}}"}"""),
            CheckViolation,
            "plans_result_requires_completed_investigation");

        Assert.Null(await StoredResultAsync(scenario.Plan.Id));
    }

    [Fact]
    public async Task Plan_cannot_be_inserted_finished_for_an_investigation_that_has_not_completed()
    {
        var (snapshot, _) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));
        var investigation = await TestGraph.AddInvestigationAsync(db, snapshot);
        var json = $$"""{"schemaVersion":"{{Version}}"}""";

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"""
                INSERT INTO plans
                    (id, investigation_id, snapshot_id, repository_id, issue_number, issue_title, issue_content_hash,
                     schema_version, result_json, created_at, updated_at)
                VALUES
                    ({Guid.NewGuid()}, {investigation.Id}, {snapshot.Id}, {snapshot.RepositoryId}, 42, 'Title', {Hash},
                     {Version}, {json}::jsonb, now(), now())
                """),
            CheckViolation,
            "plans_result_requires_completed_investigation");
    }

    [Fact]
    public async Task Completing_the_investigation_its_evidence_and_its_plan_is_one_transaction()
    {
        var scenario = await AddUnfinishedPlanAsync();
        scenario.Investigation.Start("fake-model", "plan-prompt-v1");
        await db.SaveChangesAsync();

        // The result cites evidence that is never stored: the whole save must fail, leaving the
        // investigation running and the plan unfinished.
        var missing = Guid.CreateVersion7();
        scenario.Investigation.Complete("{}");
        scenario.Plan.RecordIssue("Cap the page size", Hash);
        scenario.Plan.Complete(scenario.Investigation, Version, ResultCiting(missing).ToJson());

        await AssertRejectedAsync(() => db.SaveChangesAsync(), CheckViolation, "plans_citations_are_evidence");

        await using var fresh = database.CreateContext();
        Assert.Equal(InvestigationState.Running, (await fresh.Investigations.SingleAsync()).State);
        Assert.False((await fresh.Plans.SingleAsync()).IsFinished);
    }

    // ---- Citations refer to the investigation's snapshot ----

    [Fact]
    public async Task A_finished_plans_citations_are_evidence_rows_that_lie_in_the_investigations_snapshot()
    {
        var scenario = await AddFinishedPlanAsync();

        await using var fresh = database.CreateContext();
        var stored = PlanResult.FromJson((await fresh.Plans.SingleAsync()).ResultJson!);
        var citedIds = stored.Evidence.Select(entry => entry.Id).ToList();
        var citedSnapshots = await (
            from evidence in fresh.EvidenceRefs
            join chunk in fresh.SourceChunks on evidence.ChunkId equals chunk.Id
            where citedIds.Contains(evidence.Id)
            select new { evidence.InvestigationId, ChunkSnapshot = chunk.SnapshotId }).ToListAsync();

        Assert.Equal(citedIds.Count, citedSnapshots.Count);
        Assert.All(citedSnapshots, cited =>
        {
            Assert.Equal(scenario.Investigation.Id, cited.InvestigationId);
            Assert.Equal(scenario.Investigation.SnapshotId, cited.ChunkSnapshot);
        });
    }

    [Theory]
    [InlineData("step cites evidence of another investigation", """{"steps":[{"text":"s","citations":["OTHER"]}],"evidence":[{"id":"OWN"}]}""")]
    [InlineData("evidence lists a row of another investigation", """{"steps":[{"text":"s","citations":["OWN"]}],"evidence":[{"id":"OWN"},{"id":"OTHER"}]}""")]
    [InlineData("cites an id that is no evidence row", """{"risks":[{"text":"r","citations":["0193a5c0-0000-7000-8000-00000000ffff"]}],"evidence":[]}""")]
    [InlineData("evidence lists an id that is no evidence row", """{"evidence":[{"id":"0193a5c0-0000-7000-8000-00000000ffff"}]}""")]
    [InlineData("cites a location instead of an id", """{"summary":{"text":"s","citations":["src/App.cs:1-2"]}}""")]
    [InlineData("cites issue evidence, which is not code", """{"verification":[{"text":"v","citations":["ISSUE"]}]}""")]
    [InlineData("a file cites evidence of another investigation", """{"filesToChange":[{"path":"a.cs","change":"modify","reason":"r","citations":["OWN","OTHER"]}]}""")]
    [InlineData("a citation is not a string", """{"unknowns":[{"text":"u","citations":[null]}]}""")]
    [InlineData("citations is not a list", """{"unknowns":[{"text":"u","citations":7}]}""")]
    [InlineData("an evidence id is missing", """{"evidence":[{"path":"src/App.cs"}]}""")]
    public async Task Plan_result_cannot_cite_anything_but_code_evidence_of_its_own_investigation(string what, string body)
    {
        var scenario = await AddUnfinishedPlanAsync();
        var own = await AddEvidenceAsync(scenario.Investigation, scenario.Chunk);
        var otherInvestigation = await TestGraph.AddInvestigationAsync(db, scenario.Snapshot);
        var other = await AddEvidenceAsync(otherInvestigation, scenario.Chunk);
        var issueEvidence = Guid.NewGuid();
        await InsertEvidenceAsync(issueEvidence, scenario.Investigation.Id, scenario.Snapshot.Id, chunkId: null, "issue");
        await MoveInvestigationAsync(scenario.Investigation, "completed");

        var json = $$"""{"schemaVersion":"{{Version}}",""" + body[1..]
            .Replace("OWN", own.Id.ToString())
            .Replace("OTHER", other.Id.ToString())
            .Replace("ISSUE", issueEvidence.ToString());

        await AssertRejectedAsync(
            () => FinishWithSqlAsync(scenario.Plan.Id, Version, json), CheckViolation, "plans_citations_are_evidence");
        Assert.True(await StoredResultAsync(scenario.Plan.Id) is null, what);
    }

    [Fact]
    public async Task Plan_result_citing_its_own_code_evidence_is_accepted()
    {
        var scenario = await AddUnfinishedPlanAsync();
        var own = await AddEvidenceAsync(scenario.Investigation, scenario.Chunk);
        await MoveInvestigationAsync(scenario.Investigation, "completed");
        var json = $$"""{"schemaVersion":"{{Version}}","steps":[{"text":"s","citations":["{{own.Id}}"]}],"evidence":[{"id":"{{own.Id}}"}]}""";

        await FinishWithSqlAsync(scenario.Plan.Id, Version, json);

        Assert.NotNull(await StoredResultAsync(scenario.Plan.Id));
    }

    [Fact]
    public async Task Evidence_for_a_plan_cannot_point_at_a_chunk_of_another_snapshot()
    {
        var repository = await TestGraph.AddRepositoryAsync(db);
        var (pinned, _) = await TestGraph.AddCompletedSnapshotAsync(db, repository, TestGraph.ShaA);
        var (_, foreignChunk) = await TestGraph.AddCompletedSnapshotAsync(db, repository, TestGraph.ShaB);
        var investigation = await TestGraph.AddInvestigationAsync(db, pinned);
        db.Add(Plan.Create(investigation, 42));
        await db.SaveChangesAsync();

        await AssertRejectedAsync(
            () => InsertEvidenceAsync(Guid.NewGuid(), investigation.Id, pinned.Id, foreignChunk.Id, "code"),
            ForeignKeyViolation,
            "fk_evidence_refs_source_chunks_chunk_id_snapshot_id");
    }

    [Fact]
    public async Task Evidence_of_a_finished_plan_cannot_be_changed_or_deleted_on_its_own()
    {
        var scenario = await AddFinishedPlanAsync();
        var evidenceId = scenario.Evidence!.Id;

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"DELETE FROM evidence_refs WHERE id = {evidenceId}"),
            CheckViolation,
            "evidence_refs_plan_immutable");
        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"UPDATE evidence_refs SET excerpt_hash = {Hash} WHERE id = {evidenceId}"),
            CheckViolation,
            "evidence_refs_plan_immutable");
        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"UPDATE evidence_refs SET chunk_id = NULL, source_kind = 'issue' WHERE id = {evidenceId}"),
            CheckViolation,
            "evidence_refs_plan_immutable");

        Assert.Equal(1, await db.EvidenceRefs.CountAsync());
    }

    [Fact]
    public async Task Evidence_without_a_finished_plan_can_still_be_deleted()
    {
        var scenario = await AddUnfinishedPlanAsync();
        var ofUnfinishedPlan = await AddEvidenceAsync(scenario.Investigation, scenario.Chunk);
        var question = Investigation.Create(scenario.Snapshot, InvestigationType.Question, "q");
        db.Add(question);
        await db.SaveChangesAsync();
        var ofQuestion = await AddEvidenceAsync(question, scenario.Chunk);

        var deleted = await db.Database.ExecuteSqlAsync(
            $"DELETE FROM evidence_refs WHERE id IN ({ofUnfinishedPlan.Id}, {ofQuestion.Id})");

        Assert.Equal(2, deleted);
    }

    // ---- Reviewing changes nothing but the reviewed fields ----

    [Fact]
    public async Task Marking_reviewed_stores_the_time_and_changes_nothing_else()
    {
        var scenario = await AddFinishedPlanAsync();
        var before = await RowWithoutReviewAsync(scenario.Plan.Id);
        var reviewedAt = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        Assert.True(scenario.Plan.MarkReviewed(reviewedAt));
        await db.SaveChangesAsync();

        await using var fresh = database.CreateContext();
        var stored = await fresh.Plans.SingleAsync();
        Assert.Equal(reviewedAt, stored.ReviewedAt);
        Assert.True(stored.IsReviewed);
        Assert.Equal(before, await RowWithoutReviewAsync(scenario.Plan.Id));
        Assert.Equal(InvestigationState.Completed, (await fresh.Investigations.SingleAsync()).State);
        Assert.Equal(1, await fresh.EvidenceRefs.CountAsync());
    }

    [Theory]
    [InlineData("result_json = result_json || jsonb_build_object('steps', jsonb_build_array())")]
    [InlineData("result_json = NULL, schema_version = NULL")]
    [InlineData("schema_version = 'plan-v2'")]
    [InlineData("issue_title = 'Edited after the plan was made'")]
    [InlineData("issue_content_hash = repeat('b', 64)")]
    [InlineData("issue_title = NULL, issue_content_hash = NULL")]
    [InlineData("reviewed_at = now(), issue_title = 'Edited while reviewing'")]
    [InlineData("reviewed_at = now(), result_json = result_json || jsonb_build_object('risks', jsonb_build_array('tampered'))")]
    public async Task A_finished_plan_cannot_be_changed_even_together_with_a_review(string assignment)
    {
        var scenario = await AddFinishedPlanAsync();
        var before = await RowWithoutReviewAsync(scenario.Plan.Id);
        var sql = string.Concat("UPDATE plans SET ", assignment, " WHERE id = {0}");

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlRawAsync(sql, scenario.Plan.Id), CheckViolation, "plans_finished_immutable");

        Assert.Equal(before, await RowWithoutReviewAsync(scenario.Plan.Id));
        Assert.Null(await db.Plans.AsNoTracking().Select(plan => plan.ReviewedAt).SingleAsync());
    }

    [Fact]
    public async Task Marking_reviewed_twice_keeps_the_first_time_in_the_domain_and_in_the_database()
    {
        var scenario = await AddFinishedPlanAsync();
        var planId = scenario.Plan.Id;
        var first = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        scenario.Plan.MarkReviewed(first);
        await db.SaveChangesAsync();

        // The domain treats a second review as already done: nothing is written.
        Assert.False(scenario.Plan.MarkReviewed(first.AddDays(1)));
        Assert.Equal(0, await db.SaveChangesAsync());

        // SQL that tries to move or clear the review is refused.
        var later = first.AddDays(2);
        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"UPDATE plans SET reviewed_at = {later} WHERE id = {planId}"),
            CheckViolation,
            "plans_reviewed_once");
        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"UPDATE plans SET reviewed_at = NULL WHERE id = {planId}"),
            CheckViolation,
            "plans_reviewed_once");

        // Writing the same time again changes nothing and is let through.
        Assert.Equal(1, await db.Database.ExecuteSqlAsync($"UPDATE plans SET reviewed_at = {first} WHERE id = {planId}"));

        await using var fresh = database.CreateContext();
        Assert.Equal(first, (await fresh.Plans.SingleAsync()).ReviewedAt);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("completed")]
    public async Task An_unfinished_plan_cannot_be_reviewed_whatever_state_its_investigation_is_in(string state)
    {
        var scenario = await AddUnfinishedPlanAsync();
        await MoveInvestigationAsync(scenario.Investigation, state);

        Assert.Throws<InvalidOperationException>(() => scenario.Plan.MarkReviewed(DateTimeOffset.UtcNow));
        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"UPDATE plans SET reviewed_at = now() WHERE id = {scenario.Plan.Id}"),
            CheckViolation,
            "ck_plans_reviewed_is_finished");

        Assert.Null(await db.Plans.AsNoTracking().Select(plan => plan.ReviewedAt).SingleAsync());
    }

    [Fact]
    public async Task A_finished_plan_can_be_deleted_for_retention()
    {
        var scenario = await AddFinishedPlanAsync();

        var deleted = await db.Database.ExecuteSqlAsync($"DELETE FROM plans WHERE id = {scenario.Plan.Id}");

        Assert.Equal(1, deleted);
        Assert.Equal(1, await db.Investigations.CountAsync());
    }

    // ---- The issue a plan was made from, and the shape of a finished row ----

    [Fact]
    public async Task The_issue_can_be_recorded_again_until_the_plan_is_finished()
    {
        var scenario = await AddUnfinishedPlanAsync();

        scenario.Plan.RecordIssue("First reading", Hash);
        await db.SaveChangesAsync();
        scenario.Plan.RecordIssue("Second reading", new string('b', 64));
        await db.SaveChangesAsync();

        await using var fresh = database.CreateContext();
        var stored = await fresh.Plans.SingleAsync();
        Assert.Equal("Second reading", stored.IssueTitle);
        Assert.Equal(new string('b', 64), stored.IssueContentHash);
        Assert.Equal(42, stored.IssueNumber);
        Assert.Equal(scenario.Repository.Id, stored.RepositoryId);
    }

    [Theory]
    [InlineData("issue_title = 'Title without a hash'", "ck_plans_issue_recorded")]
    [InlineData("issue_content_hash = repeat('a', 64)", "ck_plans_issue_recorded")]
    [InlineData("issue_title = 'T', issue_content_hash = 'abc'", "ck_plans_issue_content_hash")]
    [InlineData("issue_title = 'T', issue_content_hash = repeat('A', 64)", "ck_plans_issue_content_hash")]
    [InlineData("issue_title = E'two\\nlines', issue_content_hash = repeat('a', 64)", "ck_plans_issue_title_single_line")]
    [InlineData("schema_version = 'plan-v1'", "ck_plans_result_has_schema_version")]
    public async Task An_unfinished_plan_row_must_still_be_well_formed(string assignment, string constraint)
    {
        var scenario = await AddUnfinishedPlanAsync();
        var sql = string.Concat("UPDATE plans SET ", assignment, " WHERE id = {0}");

        await AssertRejectedAsync(() => db.Database.ExecuteSqlRawAsync(sql, scenario.Plan.Id), CheckViolation, constraint);
    }

    [Fact]
    public async Task An_issue_number_starts_at_one_and_a_title_is_at_most_256_characters()
    {
        var (snapshot, _) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));
        var investigation = await TestGraph.AddInvestigationAsync(db, snapshot);
        var plan = Plan.Create(investigation, 42);
        db.Add(plan);
        await db.SaveChangesAsync();
        var tooLong = new string('x', Plan.MaxIssueTitleLength + 1);

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"UPDATE plans SET issue_number = 0 WHERE id = {plan.Id}"),
            CheckViolation,
            "plans_identity_immutable");
        var second = await TestGraph.AddInvestigationAsync(db, snapshot);
        await AssertRejectedAsync(
            () => InsertPlanAsync(second.Id, snapshot.Id, snapshot.RepositoryId, issueNumber: 0),
            CheckViolation,
            "ck_plans_issue_number");

        var thrown = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"UPDATE plans SET issue_title = {tooLong}, issue_content_hash = {Hash} WHERE id = {plan.Id}"));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, thrown.SqlState);
    }

    [Theory]
    [InlineData("plan-v2", """{"schemaVersion":"plan-v1"}""", true, "ck_plans_result_schema_version_matches")]
    [InlineData("plan-v1", """{}""", true, "ck_plans_result_schema_version_matches")]
    [InlineData("plan-v1", """["plan-v1"]""", true, "ck_plans_result_schema_version_matches")]
    [InlineData("plan-v1", "\"plan-v1\"", true, "ck_plans_result_schema_version_matches")]
    [InlineData("plan-v1", """{"schemaVersion":"plan-v1"}""", false, "ck_plans_result_has_issue")]
    public async Task A_result_states_its_schema_version_and_needs_a_recorded_issue(
        string schemaVersion, string json, bool recordIssue, string constraint)
    {
        var scenario = await AddUnfinishedPlanAsync();
        await MoveInvestigationAsync(scenario.Investigation, "completed");

        await AssertRejectedAsync(
            () => FinishWithSqlAsync(scenario.Plan.Id, schemaVersion, json, recordIssue), CheckViolation, constraint);
    }

    [Fact]
    public async Task A_plan_result_round_trips_through_the_database()
    {
        var scenario = await AddUnfinishedPlanAsync();
        var code = EvidenceRef.ForCode(
            scenario.Investigation, scenario.Chunk, """{"path":"src/App.cs","startLine":1,"endLine":2}""", "line one\nline two");
        var more = EvidenceRef.ForCode(
            scenario.Investigation, scenario.Chunk, """{"path":"src/App.cs","startLine":3,"endLine":3}""", "line three");
        var result = new PlanResult(
            Version,
            PlanOutcome.Planned,
            new PlanStatement("Change line one; \"quotes\", <angle brackets> & an emoji \U0001F600 survive.", [code.Id]),
            Steps: [new PlanStatement("First\nsecond line", [code.Id, more.Id]), new PlanStatement("Then this.", [more.Id])],
            FilesToChange:
            [
                new PlanFile("src/App.cs", PlanFileChange.Modify, "It holds the lines.", [code.Id]),
                new PlanFile("src/New.cs", PlanFileChange.Create, "A new file.", []),
                new PlanFile("src/Old.cs", PlanFileChange.Delete, "No longer used.", [more.Id]),
            ],
            Risks: [new PlanStatement("A risk.", [code.Id])],
            Verification: [new PlanStatement("Run the tests.", [more.Id])],
            Unknowns: [new PlanStatement("Something nobody knows.", [])],
            Evidence:
            [
                new PlanEvidence(code.Id, "src/App.cs", 1, 2, "line one\nline two"),
                new PlanEvidence(more.Id, "src/App.cs", 3, 3, "line three"),
            ]);
        var json = result.ToJson();

        scenario.Investigation.Start("fake-model", "plan-prompt-v1");
        scenario.Investigation.Complete("{}");
        scenario.Plan.RecordIssue("Cap the page size", Hash);
        scenario.Plan.Complete(scenario.Investigation, Version, json);
        db.AddRange(code, more);
        await db.SaveChangesAsync();

        await using var fresh = database.CreateContext();
        var stored = await fresh.Plans.SingleAsync();
        var read = PlanResult.FromJson(stored.ResultJson!);

        // jsonb does not keep the text as written (key order, spacing), so compare what is read.
        Assert.Equal(json, read.ToJson());
        Assert.Equal(Version, stored.SchemaVersion);
        Assert.True(stored.IsFinished);
        Assert.False(stored.IsReviewed);
        Assert.Equal("Cap the page size", stored.IssueTitle);
        Assert.Equal(Hash, stored.IssueContentHash);
        Assert.Equal(scenario.Investigation.Id, stored.InvestigationId);
        Assert.Equal(scenario.Snapshot.Id, stored.SnapshotId);
        Assert.Equal(TimeSpan.Zero, stored.CreatedAt.Offset);
    }

    private sealed record Scenario(
        Repository Repository, IndexSnapshot Snapshot, SourceChunk Chunk, Investigation Investigation, Plan Plan, EvidenceRef? Evidence = null);

    /// <summary>A queued issue-to-plan investigation with its plan, as a request creates them.</summary>
    private async Task<Scenario> AddUnfinishedPlanAsync()
    {
        var repository = await TestGraph.AddRepositoryAsync(db);
        var (snapshot, chunk) = await TestGraph.AddCompletedSnapshotAsync(db, repository);
        var investigation = await TestGraph.AddInvestigationAsync(db, snapshot);
        var plan = Plan.Create(investigation, issueNumber: 42);
        db.Add(plan);
        await db.SaveChangesAsync();
        return new Scenario(repository, snapshot, chunk, investigation, plan);
    }

    /// <summary>Finishes a plan the way the workflow will: everything in one save.</summary>
    private async Task<Scenario> AddFinishedPlanAsync()
    {
        var scenario = await AddUnfinishedPlanAsync();
        var evidence = EvidenceRef.ForCode(
            scenario.Investigation, scenario.Chunk, """{"path":"src/App.cs","startLine":1,"endLine":2}""", "line one\nline two");

        scenario.Investigation.Start("fake-model", "plan-prompt-v1");
        scenario.Investigation.Complete("{}");
        scenario.Plan.RecordIssue("Cap the page size", Hash);
        scenario.Plan.Complete(scenario.Investigation, Version, ResultCiting(evidence.Id).ToJson());
        db.Add(evidence);
        await db.SaveChangesAsync();
        return scenario with { Evidence = evidence };
    }

    private static PlanResult ResultCiting(Guid evidenceId) => new(
        Version,
        PlanOutcome.Planned,
        new PlanStatement("Change line one.", [evidenceId]),
        Steps: [new PlanStatement("Edit the first line.", [evidenceId])],
        FilesToChange: [new PlanFile("src/App.cs", PlanFileChange.Modify, "It holds the line.", [evidenceId])],
        Risks: [],
        Verification: [],
        Unknowns: [],
        Evidence: [new PlanEvidence(evidenceId, "src/App.cs", 1, 2, "line one\nline two")]);

    private async Task<EvidenceRef> AddEvidenceAsync(Investigation investigation, SourceChunk chunk)
    {
        var evidence = EvidenceRef.ForCode(investigation, chunk, """{"path":"src/App.cs","startLine":1,"endLine":1}""", "line one");
        db.Add(evidence);
        await db.SaveChangesAsync();
        return evidence;
    }

    /// <summary>Takes a queued investigation to the named state through the domain model.</summary>
    private async Task MoveInvestigationAsync(Investigation investigation, string state)
    {
        switch (state)
        {
            case "running":
                investigation.Start("fake-model", "plan-prompt-v1");
                break;
            case "failed":
                investigation.Fail("model_error");
                break;
            case "cancelled":
                investigation.Cancel();
                break;
            case "completed":
                investigation.Start("fake-model", "plan-prompt-v1");
                investigation.Complete("{}");
                break;
        }

        await db.SaveChangesAsync();
    }

    private Task InsertPlanAsync(Guid investigationId, Guid snapshotId, Guid repositoryId, int issueNumber = 42) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO plans (id, investigation_id, snapshot_id, repository_id, issue_number, created_at, updated_at)
            VALUES ({Guid.NewGuid()}, {investigationId}, {snapshotId}, {repositoryId}, {issueNumber}, now(), now())
            """);

    private Task FinishWithSqlAsync(Guid planId, string schemaVersion, string json, bool recordIssue = true)
    {
        string? title = recordIssue ? "Cap the page size" : null;
        string? hash = recordIssue ? Hash : null;
        return db.Database.ExecuteSqlAsync($"""
            UPDATE plans
            SET issue_title = {title}, issue_content_hash = {hash}, schema_version = {schemaVersion}, result_json = {json}::jsonb
            WHERE id = {planId}
            """);
    }

    private Task InsertEvidenceAsync(Guid id, Guid investigationId, Guid snapshotId, Guid? chunkId, string sourceKind) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO evidence_refs
                (id, investigation_id, snapshot_id, chunk_id, source_kind, locator_json, excerpt_hash, created_at, updated_at)
            VALUES
                ({id}, {investigationId}, {snapshotId}, {chunkId}, {sourceKind}, {"{}"}::jsonb, {Hash}, now(), now())
            """);

    private Task<string?> StoredResultAsync(Guid planId) => db.Database
        .SqlQuery<string?>($"SELECT result_json::text AS \"Value\" FROM plans WHERE id = {planId}")
        .SingleAsync();

    /// <summary>The whole row except the two columns a review may touch.</summary>
    private Task<string> RowWithoutReviewAsync(Guid planId) => db.Database
        .SqlQuery<string>($"SELECT (to_jsonb(plans) - 'reviewed_at' - 'updated_at')::text AS \"Value\" FROM plans WHERE id = {planId}")
        .SingleAsync();

    private static async Task AssertRejectedAsync(Func<Task> act, string sqlState, string constraintName)
    {
        var thrown = await Assert.ThrowsAnyAsync<Exception>(act);
        var postgresException = thrown as PostgresException ?? thrown.InnerException as PostgresException;

        Assert.NotNull(postgresException);
        Assert.Equal(sqlState, postgresException.SqlState);
        Assert.Equal(constraintName, postgresException.ConstraintName);
    }
}
