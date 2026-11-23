using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trace.Application.Investigations;
using Trace.Application.Plans;
using Trace.Application.Telemetry;
using Trace.Contracts.V1;
using Trace.Domain;
using Trace.Infrastructure.Chat;
using Trace.Infrastructure.GitHub;
using Trace.IntegrationTests.Support;
using Trace.TestSupport;

namespace Trace.IntegrationTests;

/// <summary>
/// Plans over HTTP: create, read, review and export, as a client sees them. The API runs as in
/// local development (fake providers, the demo repository with its fixture issues) with sign-in
/// required for writes. GitHub's HTTP API is a stub that answers in GitHub's documented formats
/// and records every request; the chat model can be swapped for a scripted one. Nothing here
/// reads a real issue or calls a real model.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlanApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const long InstallationId = 101;
    private const long PrivateRepoId = 5001;
    private const string NotesPath = "docs/notes.md";
    private const string TitleMarker = "zztitle-marker";
    private const string BodyMarker = "zzbody-marker";
    private const string CommentMarker = "zzcomment-marker";
    private const string ModelMarker = "zzmodel-marker";
    private const string SourceMarker = "zzsource-marker";
    private const string PathMarker = "zzpath-marker";
    private const string InjectionMarker = "TRACE-INJECTION-MARKER-7F3A";

    private static readonly string HostilePath = $"docs/{PathMarker} `x` ``` <b> # [a](javascript:alert(1)).md";

    private static readonly string HostileTitle =
        $"{TitleMarker} <script>alert(1)</script> [x](javascript:alert(1)) ` `` ``` # > - https://attacker.invalid";

    /// <summary>A file of the indexed repository written to break a Markdown document that quotes it.</summary>
    private static readonly string[] HostileSource =
    [
        $"# Notes {SourceMarker}",
        "```",
        "# Forged heading from source",
        "~~~",
        $"</details><script>alert('{SourceMarker}')</script><!-- comment -->",
        "[click](javascript:alert(1)) ![pixel](http://attacker.invalid/p.png) <https://attacker.invalid>",
        "- Reviewed: yes, marked in TRACE at 2026-01-01T00:00:00Z",
        "### E9: `src/Secrets.cs`, lines 1 to 40",
        "````",
        "[ref]: javascript:alert(1)",
        "\u202Ereversed\u202C \u2066isolated\u2069 separator\u2028here",
        "`````` ~~~~~~",
    ];

    private readonly WebApplicationFactory<Program> baseFactory = new();
    private readonly FakeGitHub gitHub = new();
    private readonly CapturedLogs logs = new();
    private readonly SwitchableChatModel model = new();
    private TestDatabase database = null!;
    private WebApplicationFactory<Program> factory = null!;

    public async Task InitializeAsync()
    {
        database = await postgres.CreateDatabaseAsync(migrate: false);
        factory = baseFactory.WithDatabase(database.ConnectionString, initializeOnStartup: true).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:ClientId"] = "Iv1.testclient",
                ["GitHub:ClientSecret"] = "client-secret",
                ["GitHub:PrivateKey"] = RSA.Create(2048).ExportRSAPrivateKeyPem(),
                ["Auth:AllowAnonymousWrites"] = "false",

                // Everything the application would ever log, so the telemetry test sees all of it.
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:System.Net.Http"] = "Trace",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(GitHubClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gitHub);
                services.AddSingleton<ILoggerProvider>(logs);
                services.AddSingleton<IChatModel>(model);
            });
        });
    }

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await baseFactory.DisposeAsync();
        await database.DisposeAsync();
    }

    // ---- Create, read, review, export ----

    [Fact]
    public async Task A_plan_is_requested_followed_like_a_question_read_reviewed_and_exported()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = await RepositoryAsync(alice, "sample-repo");

        using var response = await CreateAsync(alice, demo.Id, 3, demo.LatestSnapshot!.Id);
        var accepted = (await response.Content.ReadFromJsonAsync<PlanResponse>())!;

        // The request only queues the work, and says where to follow it.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/api/v1/plans/{accepted.PlanId}", response.Headers.Location!.AbsolutePath);
        Assert.Contains(accepted.State, new[] { "queued", "running", "planned" });
        Assert.NotNull(accepted.JobId);
        Assert.Equal((demo.Id, demo.LatestSnapshot.Id, demo.LatestSnapshot.CommitSha, 3), (accepted.RepositoryId, accepted.SnapshotId, accepted.CommitSha, accepted.Issue.Number));
        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(response.Headers.GetValues(TraceTelemetry.CorrelationHeader)));

        var plan = await WaitAsync(alice, accepted.PlanId);

        // The read response: state, issue, pinned commit, model, prompt, usage, document, trimmed, reviewed.
        Assert.Equal(("planned", null), (plan.State, plan.ErrorCode));
        Assert.Equal((accepted.PlanId, accepted.InvestigationId, accepted.JobId), (plan.PlanId, plan.InvestigationId, plan.JobId));
        Assert.Equal(new PlanIssueResponse(3, "First page of the task list skips the oldest tasks"), plan.Issue);
        Assert.Equal(demo.LatestSnapshot.CommitSha, plan.CommitSha);
        Assert.Equal(("fake-extractive-v1", PlanPrompt.Version), (plan.ModelId, plan.PromptVersion));
        Assert.Equal(new PlanTrimmedResponse(0, 0, 0, 0, 0, 0, SummaryReplaced: false), plan.Trimmed);
        Assert.Empty(plan.Limitations);
        Assert.Null(plan.ReviewedAt);
        Assert.Null(plan.IssueCheck);
        Assert.True(plan.UpdatedAt >= plan.CreatedAt);

        var document = plan.Plan!;
        Assert.Equal("plan-v1", document.SchemaVersion);
        Assert.NotEmpty(document.Steps);
        Assert.NotEmpty(document.Evidence);
        Assert.All(document.Steps.Concat(document.Risks).Concat(document.Verification), statement => Assert.NotEmpty(statement.Citations));
        Assert.All(document.FilesToChange, file => Assert.Equal("modify", file.Change));
        Assert.Empty(Assert.Single(document.Unknowns).Citations);
        var evidenceIds = document.Evidence.Select(evidence => evidence.Id).ToHashSet();
        Assert.All(
            document.Steps.Concat(document.Risks).Concat(document.Verification).Append(document.Summary).SelectMany(s => s.Citations)
                .Concat(document.FilesToChange.SelectMany(file => file.Citations)),
            id => Assert.Contains(id, evidenceIds));

        // Evidence as a question returns it: path, lines, commit, and the text read from the snapshot.
        Assert.All(document.Evidence, evidence =>
        {
            Assert.Equal(("code", plan.CommitSha), (evidence.Source, evidence.CommitSha));
            var lines = SampleRepo.ReadLines(evidence.Path);
            Assert.Equal(string.Join('\n', lines[(evidence.StartLine - 1)..evidence.EndLine]), evidence.Excerpt);
        });

        // The same run is an investigation with a job and events, exactly as for a question.
        var investigation = await alice.GetFromJsonAsync<InvestigationResponse>($"/api/v1/investigations/{plan.InvestigationId}");
        Assert.Equal(("issue_to_plan", "completed", "Plan the change requested in issue #3."), (investigation!.Type, investigation.State, investigation.Question));
        var events = await alice.GetFromJsonAsync<List<InvestigationEventResponse>>($"/api/v1/investigations/{plan.InvestigationId}/events");
        Assert.Equal(["started", "tool_called", "completed"], events!.Select(e => e.EventType));
        var job = await alice.GetFromJsonAsync<JobResponse>($"/api/v1/jobs/{plan.JobId}");
        Assert.Equal((PlanJobHandler.JobKind, "succeeded"), (job!.Kind, job.Status));

        // Export before review.
        using var exported = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}/export");
        var markdown = await exported.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("text/markdown; charset=utf-8", exported.Content.Headers.ContentType!.ToString());
        Assert.Equal("nosniff", Assert.Single(exported.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-store", exported.Headers.CacheControl!.ToString());
        Assert.Equal("attachment", exported.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal($"trace-plan-issue-3-{plan.PlanId:N}.md", exported.Content.Headers.ContentDisposition.FileName);
        Assert.Contains("- Repository: `trace-demo/sample-repo`\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Issue number: 3\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Issue title when the plan was made: `First page of the task list skips the oldest tasks`\n", markdown, StringComparison.Ordinal);
        Assert.Contains($"- Pinned commit: `{plan.CommitSha}`\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Outcome: planned\n", markdown, StringComparison.Ordinal);
        Assert.Contains($"- Model: `fake-extractive-v1`\n- Prompt version: `{PlanPrompt.Version}`\n- Plan schema: `plan-v1`\n", markdown, StringComparison.Ordinal);
        Assert.Contains($"- Plan ID: `{plan.PlanId}`\n- Investigation ID: `{plan.InvestigationId}`\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Reviewed: no\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Steps: 0\n- Files to change: 0\n- Risks: 0\n- Verification entries: 0\n- Unknowns: 0\n- Citations discarded: 0\n", markdown, StringComparison.Ordinal);
        Assert.Contains("(not backed by evidence: the model's unsupported statement)", markdown, StringComparison.Ordinal);
        var parsed = new MarkdownOracle(markdown);
        Assert.Equal(document.Evidence.Select(evidence => evidence.Excerpt), parsed.Fences);
        Assert.Equal(
            document.Evidence.Select((evidence, index) => $"E{index + 1}: {evidence.Path}, lines {evidence.StartLine} to {evidence.EndLine}"),
            parsed.Headings.Skip(11));
        Assert.All(document.Steps, step => Assert.Contains(step.Text, parsed.CodeSpans));
        Assert.Empty(parsed.ActiveContent);

        // Review: an acknowledgement that changes the reviewed time and nothing else, anywhere.
        var before = await FingerprintAsync(plan.PlanId);
        var gitHubBefore = gitHub.Requests.Count;
        using var reviewResponse = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        var reviewed = (await reviewResponse.Content.ReadFromJsonAsync<PlanResponse>())!;
        using var againResponse = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        var again = (await againResponse.Content.ReadFromJsonAsync<PlanResponse>())!;
        var after = await FingerprintAsync(plan.PlanId);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (reviewResponse.StatusCode, againResponse.StatusCode));
        Assert.NotNull(reviewed.ReviewedAt);
        Assert.Equal(reviewed.ReviewedAt, again.ReviewedAt);
        Assert.Equal(("planned", plan.PlanId), (reviewed.State, reviewed.PlanId));
        Assert.Equal(JsonSerializer.Serialize(plan.Plan), JsonSerializer.Serialize(reviewed.Plan));
        Assert.Equal(before.PlanWithoutReview, after.PlanWithoutReview);
        Assert.Equal(before.EverythingElse, after.EverythingElse);
        Assert.Equal((null, reviewed.ReviewedAt), (before.ReviewedAt, after.ReviewedAt));
        Assert.Equal(gitHubBefore, gitHub.Requests.Count);
        Assert.Equal(reviewed.ReviewedAt, (await alice.GetFromJsonAsync<PlanResponse>($"/api/v1/plans/{plan.PlanId}"))!.ReviewedAt);

        // The export of the reviewed plan differs in its review line only, and is stable.
        var exportedAfter = await alice.GetStringAsync($"/api/v1/plans/{plan.PlanId}/export");
        var reviewLine = $"- Reviewed: yes, marked in TRACE at {reviewed.ReviewedAt.Value.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}";
        Assert.Equal(markdown.Replace("- Reviewed: no", reviewLine, StringComparison.Ordinal), exportedAfter);
        Assert.Equal(exportedAfter, await alice.GetStringAsync($"/api/v1/plans/{plan.PlanId}/export"));
        Assert.Equal(gitHubBefore, gitHub.Requests.Count);
    }

    [Fact]
    public async Task Reviews_arriving_together_all_succeed_and_the_first_time_stands()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = await RepositoryAsync(alice, "sample-repo");
        using var created = await CreateAsync(alice, demo.Id, 3, demo.LatestSnapshot!.Id);
        var plan = await WaitAsync(alice, (await created.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null)));
        var times = new List<DateTimeOffset?>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            times.Add((await response.Content.ReadFromJsonAsync<PlanResponse>())!.ReviewedAt);
            response.Dispose();
        }

        var stored = (await FingerprintAsync(plan.PlanId)).ReviewedAt;
        Assert.NotNull(stored);
        Assert.All(times, time => Assert.Equal(stored, time));
        Assert.Equal(1, logs.Lines.Count(line => line.Contains($"Plan {plan.PlanId} was marked as reviewed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Of_two_reviews_with_the_same_clock_reading_exactly_one_marks_the_plan()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = await RepositoryAsync(alice, "sample-repo");
        using var created = await CreateAsync(alice, demo.Id, 3, demo.LatestSnapshot!.Id);
        var plan = await WaitAsync(alice, (await created.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        var instant = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        await using var first = database.CreateScope();
        await using var second = database.CreateScope();
        var firstDb = first.ServiceProvider.GetRequiredService<Trace.Infrastructure.Persistence.TraceDbContext>();
        await using var transaction = await firstDb.Database.BeginTransactionAsync();
        var firstMarked = await first.ServiceProvider.GetRequiredService<IPlanStore>().MarkReviewedAsync(plan.PlanId, instant);

        // The second arrives before the first has committed, and its clock reads the same instant:
        // the database would accept its timestamp as "no change", so only the condition in the
        // statement itself can tell it that it was not the one to mark the plan.
        var waiting = second.ServiceProvider.GetRequiredService<IPlanStore>().MarkReviewedAsync(plan.PlanId, instant);
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted);
        await transaction.CommitAsync();
        var secondMarked = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        var third = await second.ServiceProvider.GetRequiredService<IPlanStore>().MarkReviewedAsync(plan.PlanId, instant.AddHours(1));

        Assert.True(firstMarked);
        Assert.False(secondMarked);
        Assert.False(third);
        Assert.Equal(instant, (await FingerprintAsync(plan.PlanId)).ReviewedAt);

        // Neither an unfinished plan nor an unknown one can be marked.
        Assert.False(await second.ServiceProvider.GetRequiredService<IPlanStore>().MarkReviewedAsync(Guid.NewGuid(), instant));
    }

    [Theory]
    [InlineData("""{}""", "snapshotId")]
    [InlineData("""{"snapshotId":"00000000-0000-0000-0000-000000000000"}""", "snapshotId")]
    [InlineData("""{"snapshotId":"{snap}","idempotencyKey":""}""", "idempotencyKey")]
    [InlineData("""{"snapshotId":"{snap}","idempotencyKey":"   "}""", "idempotencyKey")]
    public async Task Invalid_request_is_rejected_with_validation_problem_details(string bodyTemplate, string invalidField)
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = await RepositoryAsync(alice, "sample-repo");
        var body = bodyTemplate.Replace("{snap}", demo.LatestSnapshot!.Id.ToString(), StringComparison.Ordinal);

        using var response = await alice.PostAsync(
            $"/api/v1/repositories/{demo.Id}/issues/3/plans", new StringContent(body, Encoding.UTF8, "application/json"));
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(invalidField, out _));
    }

    [Fact]
    public async Task Unknown_repository_snapshot_issue_number_or_plan_is_not_found_and_a_bad_body_is_a_bad_request()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = await RepositoryAsync(alice, "sample-repo");
        var snapshot = demo.LatestSnapshot!.Id;

        using var unknownRepository = await CreateAsync(alice, Guid.NewGuid(), 3, snapshot);
        using var unknownSnapshot = await CreateAsync(alice, demo.Id, 3, Guid.NewGuid());
        using var numberZero = await CreateAsync(alice, demo.Id, 0, snapshot);
        using var numberText = await alice.PostAsJsonAsync($"/api/v1/repositories/{demo.Id}/issues/abc/plans", new CreatePlanRequest(snapshot, null));
        using var read = await alice.GetAsync($"/api/v1/plans/{Guid.NewGuid()}");
        using var readChecked = await alice.GetAsync($"/api/v1/plans/{Guid.NewGuid()}?checkIssue=true");
        using var review = await alice.PostAsync($"/api/v1/plans/{Guid.NewGuid()}/review", null);
        using var export = await alice.GetAsync($"/api/v1/plans/{Guid.NewGuid()}/export");
        using var notAnId = await alice.GetAsync("/api/v1/plans/not-an-id");
        using var notJson = await alice.PostAsync(
            $"/api/v1/repositories/{demo.Id}/issues/3/plans", new StringContent("not json at all", Encoding.UTF8, "application/json"));
        using var badFlag = await alice.GetAsync($"/api/v1/plans/{Guid.NewGuid()}?checkIssue=maybe");

        foreach (var response in new[] { unknownRepository, unknownSnapshot, numberZero, numberText, read, readChecked, review, export, notAnId })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Matches("^[0-9a-f]{32}$", Assert.Single(response.Headers.GetValues(TraceTelemetry.CorrelationHeader)));
        }

        foreach (var response in new[] { notJson, badFlag })
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        }
    }

    // ---- States ----

    [Fact]
    public async Task A_failed_plan_reports_its_fixed_outcome_and_can_be_neither_reviewed_nor_exported()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = await RepositoryAsync(alice, "sample-repo");

        // Requesting a plan does not check that the issue exists: the run finds out.
        using var response = await CreateAsync(alice, demo.Id, 999, demo.LatestSnapshot!.Id);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var plan = await WaitAsync(alice, (await response.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);

        Assert.Equal(("failed", "issue_not_found"), (plan.State, plan.ErrorCode));
        Assert.Equal(new PlanIssueResponse(999, null), plan.Issue);
        Assert.Null(plan.Plan);
        Assert.Null(plan.Trimmed);
        Assert.Null(plan.ReviewedAt);

        using var review = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        using var export = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}/export");
        var checkedPlan = await alice.GetFromJsonAsync<PlanResponse>($"/api/v1/plans/{plan.PlanId}?checkIssue=true");

        var problem = await AssertProblemAsync(review, HttpStatusCode.Conflict, "plan_not_reviewable");
        Assert.Equal("failed", problem.GetProperty("state").GetString());
        await AssertProblemAsync(export, HttpStatusCode.Conflict, "plan_not_finished");
        Assert.Equal(new PlanIssueCheckResponse("unavailable", "issue_not_recorded"), checkedPlan!.IssueCheck);
        Assert.Null((await alice.GetFromJsonAsync<PlanResponse>($"/api/v1/plans/{plan.PlanId}"))!.ReviewedAt);
    }

    [Fact]
    public async Task An_insufficient_evidence_result_is_exported_as_such_and_cannot_be_reviewed()
    {
        ScriptedChatModel scripted = null!;
        scripted = new ScriptedChatModel(
            _ => ModelTurn.Call(scripted.Search("title limit")),
            _ => Proposal.Turn(sufficientEvidence: false, unknowns: [Proposal.Statement("Where the title limit is enforced.")]));
        model.Inner = scripted;
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = await RepositoryAsync(alice, "sample-repo");

        using var response = await CreateAsync(alice, demo.Id, 5, demo.LatestSnapshot!.Id);
        var plan = await WaitAsync(alice, (await response.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);

        Assert.Equal(("insufficient_evidence", null, "scripted-v1"), (plan.State, plan.ErrorCode, plan.ModelId));
        Assert.Empty(plan.Plan!.Steps);
        Assert.Empty(plan.Plan.Evidence);
        Assert.Equal(["Where the title limit is enforced."], plan.Plan.Unknowns.Select(unknown => unknown.Text));
        Assert.NotEmpty(plan.Limitations);
        Assert.NotNull(plan.Trimmed);

        using var review = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        using var export = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}/export");
        var markdown = await export.Content.ReadAsStringAsync();

        // There is no plan in it to acknowledge.
        var problem = await AssertProblemAsync(review, HttpStatusCode.Conflict, "plan_not_reviewable");
        Assert.Equal("insufficient_evidence", problem.GetProperty("state").GetString());
        Assert.Null((await alice.GetFromJsonAsync<PlanResponse>($"/api/v1/plans/{plan.PlanId}"))!.ReviewedAt);

        // But there is a document, and the export says what it is.
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("- Outcome: insufficient evidence. TRACE could not ground a plan in the pinned snapshot, so there are no steps.\n", markdown, StringComparison.Ordinal);
        Assert.Contains("## Steps\n\nNone.\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- `Where the title limit is enforced.` (not backed by evidence: the model's unsupported statement)\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Reviewed: no\n", markdown, StringComparison.Ordinal);
        Assert.EndsWith("## Evidence\n\nNone.\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unfinished_or_cancelled_plan_can_be_read_but_neither_reviewed_nor_exported()
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Inner = new BlockingChatModel(called);
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = await RepositoryAsync(alice, "sample-repo");

        using var response = await CreateAsync(alice, demo.Id, 3, demo.LatestSnapshot!.Id);
        var accepted = (await response.Content.ReadFromJsonAsync<PlanResponse>())!;
        await called.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Running: the issue has been read, the model has not answered.
        var running = await alice.GetFromJsonAsync<PlanResponse>($"/api/v1/plans/{accepted.PlanId}");
        using var reviewRunning = await alice.PostAsync($"/api/v1/plans/{accepted.PlanId}/review", null);
        using var exportRunning = await alice.GetAsync($"/api/v1/plans/{accepted.PlanId}/export");
        Assert.Equal(("running", null), (running!.State, running.ErrorCode));
        Assert.Equal("First page of the task list skips the oldest tasks", running.Issue.Title);
        Assert.Null(running.Plan);
        Assert.Null(running.Trimmed);
        Assert.Equal("running", (await AssertProblemAsync(reviewRunning, HttpStatusCode.Conflict, "plan_not_reviewable")).GetProperty("state").GetString());
        await AssertProblemAsync(exportRunning, HttpStatusCode.Conflict, "plan_not_finished");

        // Cancelled through its job, like any investigation.
        using var cancel = await alice.PostAsync($"/api/v1/jobs/{accepted.JobId}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var cancelled = await WaitAsync(alice, accepted.PlanId);
        using var reviewCancelled = await alice.PostAsync($"/api/v1/plans/{accepted.PlanId}/review", null);
        using var exportCancelled = await alice.GetAsync($"/api/v1/plans/{accepted.PlanId}/export");

        Assert.Equal("cancelled", cancelled.State);
        Assert.Null(cancelled.Plan);
        Assert.Null(cancelled.ReviewedAt);
        Assert.Equal("cancelled", (await AssertProblemAsync(reviewCancelled, HttpStatusCode.Conflict, "plan_not_reviewable")).GetProperty("state").GetString());
        await AssertProblemAsync(exportCancelled, HttpStatusCode.Conflict, "plan_not_finished");
    }

    // ---- Authorization ----

    [Fact]
    public async Task A_caller_cannot_create_read_review_or_export_a_plan_of_a_repository_they_cannot_see()
    {
        gitHub.Issues[42] = new FakeIssue($"First page of the task list {TitleMarker}", $"The body {BodyMarker}");
        using var alice = NewClient();
        using var bob = NewClient();
        using var anonymous = NewClient();
        await SignInAsync(alice, "alice");
        await SignInAsync(bob, "bob");
        var repository = await IndexPrivateRepositoryAsync(alice);
        var snapshot = repository.LatestSnapshot!.Id;
        using var created = await CreateAsync(alice, repository.Id, 42, snapshot);
        var plan = await WaitAsync(alice, (await created.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        Assert.Equal("planned", plan.State);

        // A second instance of the API where anonymous callers may start work (as in local development).
        using var openFactory = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:AllowAnonymousWrites"] = "true" })));
        using var anonymousWithWrites = openFactory.CreateClient();
        var requestsBefore = gitHub.Requests.Count;
        var before = await FingerprintAsync(plan.PlanId);

        // Bob is signed in and knows every ID; so does the anonymous caller.
        var forbidden = new List<HttpResponseMessage>();
        foreach (var client in new[] { bob, anonymousWithWrites })
        {
            forbidden.Add(await CreateAsync(client, repository.Id, 42, snapshot));
            forbidden.Add(await client.GetAsync($"/api/v1/plans/{plan.PlanId}"));
            forbidden.Add(await client.GetAsync($"/api/v1/plans/{plan.PlanId}?checkIssue=true"));
            forbidden.Add(await client.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null));
            forbidden.Add(await client.GetAsync($"/api/v1/plans/{plan.PlanId}/export"));
            forbidden.Add(await client.GetAsync($"/api/v1/investigations/{plan.InvestigationId}"));
            forbidden.Add(await client.GetAsync($"/api/v1/investigations/{plan.InvestigationId}/events"));
            forbidden.Add(await client.PostAsync($"/api/v1/jobs/{plan.JobId}/cancel", null));
        }

        forbidden.Add(await anonymous.GetAsync($"/api/v1/plans/{plan.PlanId}"));
        forbidden.Add(await anonymous.GetAsync($"/api/v1/plans/{plan.PlanId}?checkIssue=true"));
        forbidden.Add(await anonymous.GetAsync($"/api/v1/plans/{plan.PlanId}/export"));

        Assert.Equal(19, forbidden.Count);
        foreach (var response in forbidden)
        {
            // The same 404 as a repository or a plan that does not exist: no code, nothing of the plan.
            var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound, code: null);
            Assert.DoesNotContain(TitleMarker, problem.GetRawText(), StringComparison.Ordinal);
            response.Dispose();
        }

        // Where sign-in is required, an anonymous caller is turned away before anything is looked up.
        using var anonymousCreate = await CreateAsync(anonymous, repository.Id, 42, snapshot);
        using var anonymousReview = await anonymous.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        var demo = await RepositoryAsync(anonymous, "sample-repo");
        using var anonymousDemoCreate = await CreateAsync(anonymous, demo.Id, 3, demo.LatestSnapshot!.Id);
        Assert.All(new[] { anonymousCreate, anonymousReview, anonymousDemoCreate }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));

        // Nothing was fetched from GitHub for any of them, nothing was stored and nothing was marked.
        Assert.Equal(requestsBefore, gitHub.Requests.Count);
        Assert.Equal(before, await FingerprintAsync(plan.PlanId));
        await using (var db = database.CreateContext())
        {
            Assert.Equal(1, await db.Plans.CountAsync());
        }

        // With anonymous writes on, the public demo repository can be planned for without signing in.
        using var openDemoCreate = await CreateAsync(anonymousWithWrites, demo.Id, 3, demo.LatestSnapshot.Id);
        Assert.Equal(HttpStatusCode.Accepted, openDemoCreate.StatusCode);
        var openPlan = await WaitAsync(anonymousWithWrites, (await openDemoCreate.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        using var openReview = await anonymousWithWrites.PostAsync($"/api/v1/plans/{openPlan.PlanId}/review", null);
        Assert.Equal(HttpStatusCode.OK, openReview.StatusCode);

        // Alice can do all of it; once her installation is suspended, she cannot either.
        using var aliceExport = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}/export");
        Assert.Equal(HttpStatusCode.OK, aliceExport.StatusCode);
        await using (var db = database.CreateContext())
        {
            (await db.GitHubInstallations.SingleAsync()).SetStatus(InstallationStatus.Suspended);
            await db.SaveChangesAsync();
        }

        using var suspendedCreate = await CreateAsync(alice, repository.Id, 42, snapshot);
        using var suspendedRead = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}");
        using var suspendedReview = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        using var suspendedExport = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}/export");
        Assert.All(
            new[] { suspendedCreate, suspendedRead, suspendedReview, suspendedExport },
            response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Null((await FingerprintAsync(plan.PlanId)).ReviewedAt);
    }

    // ---- Idempotency and rate limiting, as for questions ----

    [Fact]
    public async Task Repeated_request_with_an_idempotency_key_returns_the_same_plan()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = await RepositoryAsync(alice, "sample-repo");
        var snapshot = demo.LatestSnapshot!.Id;
        var key = Guid.NewGuid().ToString();
        var questionKey = Guid.NewGuid().ToString();

        using var first = await CreateAsync(alice, demo.Id, 3, snapshot, key);
        using var second = await CreateAsync(alice, demo.Id, 3, snapshot, key);
        using var otherIssue = await CreateAsync(alice, demo.Id, 5, snapshot, key);
        using var question = await alice.PostAsJsonAsync(
            "/api/v1/investigations", new CreateInvestigationRequest(demo.Id, snapshot, "question", "Where is paging done?", questionKey));
        using var planWithQuestionKey = await CreateAsync(alice, demo.Id, 3, snapshot, questionKey);
        using var questionWithPlanKey = await alice.PostAsJsonAsync(
            "/api/v1/investigations", new CreateInvestigationRequest(demo.Id, snapshot, "question", "Where is paging done?", key));
        using var withoutKey = await CreateAsync(alice, demo.Id, 3, snapshot);
        using var withoutKeyAgain = await CreateAsync(alice, demo.Id, 3, snapshot);

        var firstPlan = (await first.Content.ReadFromJsonAsync<PlanResponse>())!;
        var secondPlan = (await second.Content.ReadFromJsonAsync<PlanResponse>())!;
        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (first.StatusCode, second.StatusCode));
        Assert.Equal(
            (firstPlan.PlanId, firstPlan.InvestigationId, firstPlan.JobId),
            (secondPlan.PlanId, secondPlan.InvestigationId, secondPlan.JobId));
        Assert.Equal(first.Headers.Location, second.Headers.Location);

        // The same key for a different issue, or across a question and a plan, is a conflict.
        Assert.Equal(HttpStatusCode.Accepted, question.StatusCode);
        foreach (var conflict in new[] { otherIssue, planWithQuestionKey, questionWithPlanKey })
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);
        }

        // Without a key every request is a new plan.
        Assert.NotEqual(
            (await withoutKey.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId,
            (await withoutKeyAgain.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);

        // The replay is still the same plan once it has finished, and nothing was stored twice.
        var finished = await WaitAsync(alice, firstPlan.PlanId);
        using var third = await CreateAsync(alice, demo.Id, 3, snapshot, key);
        var thirdPlan = (await third.Content.ReadFromJsonAsync<PlanResponse>())!;
        Assert.Equal((HttpStatusCode.Accepted, finished.PlanId, "planned"), (third.StatusCode, thirdPlan.PlanId, thirdPlan.State));
        await using var db = database.CreateContext();
        Assert.Equal(3, await db.Plans.CountAsync());
        Assert.Equal(1, await db.Investigations.CountAsync(i => i.IdempotencyKey == key));
        Assert.Equal(1, await db.Jobs.CountAsync(j => j.DedupeKey == firstPlan.InvestigationId.ToString()));
    }

    [Fact]
    public async Task Requesting_plans_is_rate_limited_per_client_and_reading_them_is_not()
    {
        using var limitedFactory = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:WritesPerMinute"] = "2",
                ["Auth:AllowAnonymousWrites"] = "true",
            })));
        using var limited = limitedFactory.CreateClient();
        var demo = await RepositoryAsync(limited, "sample-repo");

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++)
        {
            responses.Add(await CreateAsync(limited, demo.Id, 3, demo.LatestSnapshot!.Id));
        }

        Assert.Equal(
            [HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            responses.Select(response => response.StatusCode));

        // The limit is the one questions share: a question is refused now too.
        using var question = await limited.PostAsJsonAsync(
            "/api/v1/investigations", new CreateInvestigationRequest(demo.Id, demo.LatestSnapshot!.Id, "question", "Where is paging done?", null));
        Assert.Equal(HttpStatusCode.TooManyRequests, question.StatusCode);

        // Reading, exporting and reviewing are not limited.
        var plan = await WaitAsync(limited, (await responses[0].Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        for (var i = 0; i < 5; i++)
        {
            using var read = await limited.GetAsync($"/api/v1/plans/{plan.PlanId}");
            using var export = await limited.GetAsync($"/api/v1/plans/{plan.PlanId}/export");
            using var review = await limited.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
            Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK), (read.StatusCode, export.StatusCode, review.StatusCode));
        }

        responses.ForEach(response => response.Dispose());
    }

    // ---- Has the issue changed? ----

    [Fact]
    public async Task Issue_change_is_reported_only_when_asked_for_and_never_fails_the_read()
    {
        gitHub.Issues[42] = new FakeIssue("First page of the task list skips tasks", "Page 1 returns tasks 21 to 25.") { Comments = { "I can reproduce it." } };
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var repository = await IndexPrivateRepositoryAsync(alice);
        using var created = await CreateAsync(alice, repository.Id, 42, repository.LatestSnapshot!.Id);
        var plan = await WaitAsync(alice, (await created.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        var url = $"/api/v1/plans/{plan.PlanId}";

        // Not asked: nothing is fetched.
        var requests = gitHub.Requests.Count;
        var plain = await alice.GetFromJsonAsync<PlanResponse>(url);
        var explicitlyOff = await alice.GetFromJsonAsync<PlanResponse>($"{url}?checkIssue=false");
        Assert.Null(plain!.IssueCheck);
        Assert.Null(explicitlyOff!.IssueCheck);
        Assert.Equal(requests, gitHub.Requests.Count);

        // Asked: the issue is read again (GET) and compared.
        var unchanged = await alice.GetFromJsonAsync<PlanResponse>($"{url}?checkIssue=true");
        Assert.Equal(new PlanIssueCheckResponse("unchanged", null), unchanged!.IssueCheck);
        Assert.True(gitHub.Requests.Count > requests);

        // Closing or relabeling is not a change to what the plan was made from.
        gitHub.Issues[42].State = "closed";
        gitHub.Issues[42].Labels.Add("wontfix");
        Assert.Equal("unchanged", (await alice.GetFromJsonAsync<PlanResponse>($"{url}?checkIssue=true"))!.IssueCheck!.Status);

        // An edited body, an edited title or a new comment is.
        gitHub.Issues[42].Body = "Page 1 returns tasks 21 to 25. Edit: also page 2.";
        Assert.Equal(new PlanIssueCheckResponse("changed", null), (await alice.GetFromJsonAsync<PlanResponse>($"{url}?checkIssue=true"))!.IssueCheck);
        gitHub.Issues[42].Body = "Page 1 returns tasks 21 to 25.";
        Assert.Equal("unchanged", (await alice.GetFromJsonAsync<PlanResponse>($"{url}?checkIssue=true"))!.IssueCheck!.Status);
        gitHub.Issues[42].Comments.Add("Still happening.");
        Assert.Equal("changed", (await alice.GetFromJsonAsync<PlanResponse>($"{url}?checkIssue=true"))!.IssueCheck!.Status);

        // GitHub failing, or the issue being gone, is reported inside a successful read of the same plan.
        gitHub.Override = request => request.RequestUri!.AbsolutePath.Contains("/issues", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : null;
        using var failing = await alice.GetAsync($"{url}?checkIssue=true");
        var failed = await failing.Content.ReadFromJsonAsync<PlanResponse>();
        Assert.Equal(HttpStatusCode.OK, failing.StatusCode);
        Assert.Equal(new PlanIssueCheckResponse("unavailable", "github_unavailable"), failed!.IssueCheck);
        Assert.Equal(("planned", plan.PlanId), (failed.State, failed.PlanId));
        Assert.NotNull(failed.Plan);

        gitHub.Override = null;
        gitHub.Issues.TryRemove(42, out _);
        Assert.Equal(
            new PlanIssueCheckResponse("unavailable", "issue_not_found"),
            (await alice.GetFromJsonAsync<PlanResponse>($"{url}?checkIssue=true"))!.IssueCheck);
        gitHub.GrantsIssuesPermission = false;
        Assert.Equal(
            new PlanIssueCheckResponse("unavailable", "issues_permission_missing"),
            (await alice.GetFromJsonAsync<PlanResponse>($"{url}?checkIssue=true"))!.IssueCheck);

        // The demo repository's fixture issue is compared without any call to GitHub.
        var demo = await RepositoryAsync(alice, "sample-repo");
        using var demoCreated = await CreateAsync(alice, demo.Id, 3, demo.LatestSnapshot!.Id);
        var demoPlan = await WaitAsync(alice, (await demoCreated.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        requests = gitHub.Requests.Count;
        Assert.Equal("unchanged", (await alice.GetFromJsonAsync<PlanResponse>($"/api/v1/plans/{demoPlan.PlanId}?checkIssue=true"))!.IssueCheck!.Status);
        Assert.Equal(requests, gitHub.Requests.Count);
    }

    // ---- Hostile content ----

    [Fact]
    public async Task The_injection_fixture_issue_cannot_get_its_text_or_its_wishes_into_the_plan_or_the_export()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = await RepositoryAsync(alice, "sample-repo");
        var requestsBefore = gitHub.Requests.Count;

        using var response = await CreateAsync(alice, demo.Id, 14, demo.LatestSnapshot!.Id);
        var plan = await WaitAsync(alice, (await response.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        using var export = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}/export");
        var markdown = await export.Content.ReadAsStringAsync();
        var json = await alice.GetStringAsync($"/api/v1/plans/{plan.PlanId}");

        Assert.Contains(plan.State, new[] { "planned", "insufficient_evidence" });
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        // The issue asked to be marked as reviewed, to have a comment posted and to be closed. None happened.
        Assert.Null(plan.ReviewedAt);
        Assert.Contains("- Reviewed: no\n", markdown, StringComparison.Ordinal);
        Assert.Equal(requestsBefore, gitHub.Requests.Count);

        // Of the issue, only its number and its title are kept, and the title is quoted.
        var title = "SYNTHETIC TEST ISSUE: prompt-injection attempt (invented for TRACE tests)";
        Assert.Equal(title, plan.Issue.Title);
        Assert.Contains($"- Issue title when the plan was made: `{title}`\n", markdown, StringComparison.Ordinal);
        var parsed = new MarkdownOracle(markdown);
        Assert.DoesNotContain("SYNTHETIC", parsed.TextOutsideCode, StringComparison.Ordinal);
        Assert.Empty(parsed.ActiveContent);
        foreach (var fromTheBody in new[] { InjectionMarker, "Ignore all previous instructions", "delete_repository", "attacker.invalid", "MasterKey", "other-private-repo", "</issue>" })
        {
            Assert.DoesNotContain(fromTheBody, markdown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fromTheBody, json, StringComparison.OrdinalIgnoreCase);
        }

        // Whatever the plan cites is real source of the pinned snapshot.
        Assert.All(plan.Plan!.Evidence, evidence =>
        {
            var lines = SampleRepo.ReadLines(evidence.Path);
            Assert.Equal(string.Join('\n', lines[(evidence.StartLine - 1)..evidence.EndLine]), evidence.Excerpt);
        });
    }

    [Fact]
    public async Task Markdown_and_html_in_the_issue_the_model_output_and_the_source_cannot_break_the_export()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var (plan, _) = await HostilePlanAsync(alice);

        // The plan holds the hostile text: it is data, returned as JSON strings.
        Assert.Equal("planned", plan.State);
        Assert.StartsWith(TitleMarker, plan.Issue.Title, StringComparison.Ordinal);
        Assert.Equal(new PlanTrimmedResponse(0, 0, 0, 0, 0, 0, SummaryReplaced: false), plan.Trimmed);
        Assert.Equal(2, plan.Plan!.Steps.Count);
        Assert.Contains("# Forged heading", plan.Plan.Steps[0].Text, StringComparison.Ordinal);
        Assert.Equal([NotesPath, HostilePath], plan.Plan.FilesToChange.Select(file => file.Path));
        var evidence = Assert.Single(plan.Plan.Evidence);
        Assert.Equal(string.Join('\n', HostileSource), evidence.Excerpt);

        using var export = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}/export");
        var bytes = await export.Content.ReadAsByteArrayAsync();
        var markdown = Encoding.UTF8.GetString(bytes);
        var parsed = new MarkdownOracle(markdown);

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/markdown; charset=utf-8", export.Content.Headers.ContentType!.ToString());
        Assert.Equal("nosniff", Assert.Single(export.Headers.GetValues("X-Content-Type-Options")));

        // The file name is the fixed pattern: nothing of the title, the model's text or a path.
        Assert.Equal($"trace-plan-issue-66-{plan.PlanId:N}.md", export.Content.Headers.ContentDisposition!.FileName);
        Assert.Matches(@"\Aattachment; filename=trace-plan-issue-66-[0-9a-f]{32}\.md\z", export.Content.Headers.ContentDisposition.ToString());

        // An independent Markdown parser finds no HTML, comment, script, image, link, autolink,
        // link definition or table, and exactly the headings TRACE writes.
        Assert.Empty(parsed.ActiveContent);
        Assert.Equal(
            [
                "Implementation plan for issue 66", "Plan record", "How to read this", "Summary", "Steps", "Files to change",
                "Risks", "Verification", "Unknowns", "Removed by validation", "Evidence",
                $"E1: {NotesPath}, lines 1 to {HostileSource.Length}",
            ],
            parsed.Headings);

        // None of the hostile text is outside code formatting; all of it is inside.
        foreach (var hostile in new[] { TitleMarker, ModelMarker, SourceMarker, PathMarker, "Forged", "attacker", "alert", "javascript", "E9" })
        {
            Assert.DoesNotContain(hostile, parsed.TextOutsideCode, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(parsed.CodeSpans, span => span.StartsWith($"{TitleMarker} <script>alert(1)</script>", StringComparison.Ordinal));
        Assert.Contains(HostilePath, parsed.CodeSpans);
        Assert.Equal(7, parsed.CodeSpans.Count(span => span.Contains(ModelMarker, StringComparison.Ordinal)));
        var fence = Assert.Single(parsed.Fences);
        Assert.Contains("# Forged heading from source", fence, StringComparison.Ordinal);
        Assert.Contains($"</details><script>alert('{SourceMarker}')</script><!-- comment -->", fence, StringComparison.Ordinal);
        Assert.Contains("`````` ~~~~~~", fence, StringComparison.Ordinal);

        // The same on the raw lines, without a parser: one review line, one evidence heading, two
        // steps, and the fence is longer than any run of backticks in the source it quotes.
        var outside = MarkdownOracle.OutsideFences(markdown);
        Assert.Equal(["- Reviewed: no"], outside.Where(line => line.Contains("Reviewed:", StringComparison.Ordinal) && !line.Contains('`', StringComparison.Ordinal)));
        Assert.Equal(1, outside.Count(line => line.StartsWith("- Reviewed:", StringComparison.Ordinal)));
        Assert.Equal(1, outside.Count(line => line.StartsWith("### ", StringComparison.Ordinal)));
        Assert.Equal(12, outside.Count(line => line.StartsWith('#')));
        Assert.Equal(["1. ", "2. "], outside.Where(line => line.Length > 2 && char.IsAsciiDigit(line[0])).Select(line => line[..3]));
        Assert.Contains("```````", markdown.Split('\n'));

        // Nothing that breaks a line in some readers, hides text or reorders it; valid UTF-8 throughout.
        Assert.DoesNotContain(markdown, c => c is '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029' or '\0' or '\u001B');
        Assert.DoesNotContain(markdown, c => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\u061C' or '\uFEFF');
        Assert.Equal(bytes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(markdown));

        // A forged review line in the text does not make the plan reviewed; a real review shows once.
        using var review = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        var reviewed = (await review.Content.ReadFromJsonAsync<PlanResponse>())!;
        var after = await alice.GetStringAsync($"/api/v1/plans/{plan.PlanId}/export");
        var reviewLine = $"- Reviewed: yes, marked in TRACE at {reviewed.ReviewedAt!.Value.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}";
        Assert.Equal(markdown.Replace("- Reviewed: no\n", reviewLine + "\n", StringComparison.Ordinal), after);
        Assert.Equal([reviewLine], MarkdownOracle.OutsideFences(after).Where(line => line.StartsWith("- Reviewed:", StringComparison.Ordinal)));
    }

    // ---- Nothing writes to GitHub ----

    [Fact]
    public async Task Creating_reading_reviewing_and_exporting_a_plan_only_ever_gets_from_github()
    {
        gitHub.Issues[42] = new FakeIssue("First page of the task list skips tasks", "Page 1 returns tasks 21 to 25.") { Comments = { "I can reproduce it." } };
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var repository = await IndexPrivateRepositoryAsync(alice);
        var snapshot = repository.LatestSnapshot!.Id;

        // Everything the feature does, including its failure paths.
        using var created = await CreateAsync(alice, repository.Id, 42, snapshot, "once");
        using var replayed = await CreateAsync(alice, repository.Id, 42, snapshot, "once");
        var plan = await WaitAsync(alice, (await created.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        Assert.Equal("planned", plan.State);
        await alice.GetFromJsonAsync<PlanResponse>($"/api/v1/plans/{plan.PlanId}");
        await alice.GetFromJsonAsync<PlanResponse>($"/api/v1/plans/{plan.PlanId}?checkIssue=true");
        using var missingCreated = await CreateAsync(alice, repository.Id, 404, snapshot);
        var missing = await WaitAsync(alice, (await missingCreated.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        Assert.Equal(("failed", "issue_not_found"), (missing.State, missing.ErrorCode));

        // Reviewing and exporting call nothing outside TRACE at all.
        var beforeReview = gitHub.Requests.Count;
        using var review = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        using var reviewAgain = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        using var export = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}/export");
        using var exportAgain = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}/export");
        using var reviewFailed = await alice.PostAsync($"/api/v1/plans/{missing.PlanId}/review", null);
        using var exportFailed = await alice.GetAsync($"/api/v1/plans/{missing.PlanId}/export");
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK), (review.StatusCode, reviewAgain.StatusCode, export.StatusCode, exportAgain.StatusCode));
        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (reviewFailed.StatusCode, exportFailed.StatusCode));
        Assert.Equal(beforeReview, gitHub.Requests.Count);

        // The API itself offers no way to change, delete or list a plan, or to push one anywhere.
        using var put = await alice.PutAsJsonAsync($"/api/v1/plans/{plan.PlanId}", new { reviewedAt = (string?)null });
        using var patch = await alice.PatchAsJsonAsync($"/api/v1/plans/{plan.PlanId}", new { state = "planned" });
        using var delete = await alice.DeleteAsync($"/api/v1/plans/{plan.PlanId}");
        using var deleteReview = await alice.DeleteAsync($"/api/v1/plans/{plan.PlanId}/review");
        using var postExport = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/export", null);
        using var getReview = await alice.GetAsync($"/api/v1/plans/{plan.PlanId}/review");
        using var list = await alice.GetAsync("/api/v1/plans");
        using var comment = await alice.PostAsJsonAsync($"/api/v1/plans/{plan.PlanId}/comment", new { body = "posted" });
        using var pullRequest = await alice.PostAsJsonAsync($"/api/v1/plans/{plan.PlanId}/pull-request", new { });
        Assert.All(new[] { put, patch, delete, deleteReview, postExport, getReview }, response => Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode));
        Assert.All(new[] { list, comment, pullRequest }, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));

        // Every request the stub received: issues were read with GET, and the only requests that
        // are not GET are the two GitHub's authentication requires.
        var requests = gitHub.Requests.ToList();
        var aboutTheRepository = requests.Where(r => r.Path.StartsWith("/repos/", StringComparison.Ordinal)).ToList();
        Assert.Contains(aboutTheRepository, r => r.Path == "/repos/acme/private-repo/issues/42");
        Assert.Contains(aboutTheRepository, r => r.Path == "/repos/acme/private-repo/issues/42/comments");
        Assert.Contains(aboutTheRepository, r => r.Path == "/repos/acme/private-repo/issues/404");
        Assert.All(aboutTheRepository, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(
            ["/app/installations/101/access_tokens", "/login/oauth/access_token"],
            requests.Where(r => r.Method != HttpMethod.Get).Select(r => r.Path).Distinct().Order());
        Assert.All(requests.Where(r => r.Method != HttpMethod.Get), r => Assert.Equal(HttpMethod.Post, r.Method));
        Assert.All(requests.Where(r => r.Method == HttpMethod.Post), r => Assert.DoesNotContain("issues", r.Path, StringComparison.OrdinalIgnoreCase));

        // Each installation token was asked for this one repository, read-only.
        Assert.NotEmpty(gitHub.TokenRequests);
        Assert.All(gitHub.TokenRequests, document =>
        {
            Assert.Equal([PrivateRepoId], document.RootElement.GetProperty("repository_ids").EnumerateArray().Select(id => id.GetInt64()));
            Assert.All(document.RootElement.GetProperty("permissions").EnumerateObject(), permission => Assert.Equal("read", permission.Value.GetString()));
        });
    }

    // ---- Telemetry ----

    [Fact]
    public async Task Spans_and_logs_of_the_plan_endpoints_carry_identifiers_counts_and_outcome_names_but_no_issue_plan_or_source_text()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");

        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            // Every source in the process: TRACE's own spans and those of ASP.NET Core and HttpClient.
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var (plan, repository) = await HostilePlanAsync(alice);
        var read = await alice.GetStringAsync($"/api/v1/plans/{plan.PlanId}?checkIssue=true");
        using var review = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        using var reviewAgain = await alice.PostAsync($"/api/v1/plans/{plan.PlanId}/review", null);
        var markdown = await alice.GetStringAsync($"/api/v1/plans/{plan.PlanId}/export");
        using var missingCreated = await CreateAsync(alice, repository.Id, 404, repository.LatestSnapshot!.Id);
        var missing = await WaitAsync(alice, (await missingCreated.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        using var reviewFailed = await alice.PostAsync($"/api/v1/plans/{missing.PlanId}/review", null);
        using var exportFailed = await alice.GetAsync($"/api/v1/plans/{missing.PlanId}/export");
        using var exportUnknown = await alice.GetAsync($"/api/v1/plans/{Guid.NewGuid()}/export");

        // The responses did contain the text, so its absence below means something.
        foreach (var marker in new[] { TitleMarker, ModelMarker, SourceMarker, PathMarker })
        {
            Assert.Contains(marker, read, StringComparison.Ordinal);
            Assert.Contains(marker, markdown, StringComparison.Ordinal);
        }

        Assert.Contains("\"issueCheck\":{\"status\":\"unchanged\"", read, StringComparison.Ordinal);

        // What the new spans do say.
        var ours = spans.Where(span => span.Source.Name == TraceTelemetry.SourceName && span.OperationName.StartsWith("plan.", StringComparison.Ordinal)).ToList();
        var marked = ours.Single(span => span.OperationName == "plan.review" && (string?)span.GetTagItem("trace.plan.review.outcome") == "marked");
        Assert.Equal(plan.PlanId, marked.GetTagItem("trace.plan_id"));
        Assert.Equal("planned", marked.GetTagItem("trace.plan.state"));
        Assert.Contains(ours, span => (string?)span.GetTagItem("trace.plan.review.outcome") == "already_reviewed");
        Assert.Contains(ours, span => (string?)span.GetTagItem("trace.plan.review.outcome") == "not_reviewable" && (string?)span.GetTagItem("trace.plan.state") == "failed");
        var exported = ours.Single(span => span.OperationName == "plan.export" && (string?)span.GetTagItem("trace.plan.export.outcome") == "exported");
        Assert.Equal(plan.PlanId, exported.GetTagItem("trace.plan_id"));
        Assert.Equal(Encoding.UTF8.GetByteCount(markdown), exported.GetTagItem("trace.plan.export.bytes"));
        Assert.Equal(2, exported.GetTagItem("trace.plan.steps"));
        Assert.Equal(1, exported.GetTagItem("trace.plan.evidence"));
        Assert.Contains(ours, span => (string?)span.GetTagItem("trace.plan.export.outcome") == "not_finished");
        Assert.Contains(ours, span => (string?)span.GetTagItem("trace.plan.export.outcome") == "not_found");
        Assert.Contains(ours, span => span.OperationName == "plan.issue_check" && (string?)span.GetTagItem("trace.plan.issue_check.outcome") == "unchanged");
        Assert.Contains(logs.Lines, line => line.Contains($"Plan {plan.PlanId} was marked as reviewed", StringComparison.Ordinal));
        Assert.Equal(1, logs.Lines.Count(line => line.Contains("was marked as reviewed", StringComparison.Ordinal)));

        // And what nothing says: spans of every source, their tags, events, baggage and status, and every log line.
        var recorded = spans
            .SelectMany(span => span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
                .Concat(span.Events.SelectMany(e => e.Tags.Select(tag => $"{tag.Key}={tag.Value}").Append(e.Name)))
                .Concat(span.Baggage.Select(item => $"{item.Key}={item.Value}"))
                .Append(span.DisplayName)
                .Append(span.StatusDescription ?? string.Empty))
            .Concat(logs.Lines)
            .ToList();
        Assert.Contains(recorded, text => text.Contains($"/api/v1/plans/{plan.PlanId}/export", StringComparison.Ordinal));
        foreach (var forbidden in new[] { TitleMarker, BodyMarker, CommentMarker, ModelMarker, SourceMarker, PathMarker, "Forged heading", "attacker.invalid", "javascript:" })
        {
            Assert.DoesNotContain(recorded, text => text.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// A plan whose issue title, model output and cited source are all written to break a
    /// Markdown document: alice's private repository is indexed from a directory holding the
    /// hostile file, the issue comes from the GitHub stub, and the model is scripted.
    /// </summary>
    private async Task<(PlanResponse Plan, RepositoryResponse Repository)> HostilePlanAsync(HttpClient alice)
    {
        gitHub.Issues[66] = new FakeIssue(HostileTitle, $"Body {BodyMarker}\n\n# Heading in the body\n<script>alert(1)</script>")
        {
            Comments = { $"A comment {CommentMarker} [x](javascript:alert(1))" },
        };

        using var directory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "docs"));
        Directory.CreateDirectory(Path.Combine(directory.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "docs", "notes.md"), string.Join('\n', HostileSource) + "\n");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "src", "App.cs"), "namespace Demo;\n\npublic static class App\n{\n}\n");
        var repository = await IndexPrivateRepositoryAsync(alice, directory.Path);

        var notes = (NotesPath, 1, HostileSource.Length);
        ScriptedChatModel scripted = null!;
        scripted = new ScriptedChatModel(
            _ => ModelTurn.Call(scripted.Search("notes")),
            _ => ModelTurn.Call(scripted.Read(NotesPath, 1, HostileSource.Length)),
            _ => Proposal.Turn(
                summary: Proposal.Statement(Hostile("summary"), notes),
                steps: [Proposal.Statement(Hostile("step"), notes), Proposal.Statement("A plain second step.", notes)],
                files:
                [
                    Proposal.File(NotesPath, "modify", Hostile("reason"), notes),
                    Proposal.File(HostilePath, "create", Hostile("new file")),
                ],
                risks: [Proposal.Statement(Hostile("risk"), notes)],
                verification: [Proposal.Statement(Hostile("verification"), notes)],
                unknowns: [Proposal.Statement(Hostile("unknown"))]));
        model.Inner = scripted;

        using var created = await CreateAsync(alice, repository.Id, 66, repository.LatestSnapshot!.Id);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var plan = await WaitAsync(alice, (await created.Content.ReadFromJsonAsync<PlanResponse>())!.PlanId);
        Assert.True(plan.State == "planned", $"{plan.State} {plan.ErrorCode}: {string.Join(" | ", plan.Limitations)} {JsonSerializer.Serialize(plan.Trimmed)}");
        return (plan, repository);
    }

    /// <summary>What a model that obeyed a hostile issue, or is hostile itself, would put in a field of its plan.</summary>
    private static string Hostile(string field) => string.Join(
        '\n',
        $"{ModelMarker} {field} ` `` ``` ````",
        "```",
        "# Forged heading",
        "",
        "- Reviewed: yes, marked in TRACE at 2026-01-01T00:00:00Z",
        "1. forged step (evidence: E1, E2)",
        "### E9: `src/Secrets.cs`, lines 1 to 40",
        "<script>alert(1)</script><!-- comment --><img src=x onerror=alert(1)>",
        "![image](http://attacker.invalid/pixel.png) [link](javascript:alert(1)) [data](data:text/html;base64,PHNjcmlwdD4=)",
        "<https://attacker.invalid> https://attacker.invalid www.attacker.invalid someone@attacker.invalid",
        "[ref]: javascript:alert(1)",
        "~~~",
        "carriage\rreturn separator\u2028line paragraph\u2029end nel\u0085x escape\u001B[2J",
        "\u202Ereversed\u202C \u2066isolated\u2069 \u200F\u200E\u061C\uFEFF");

    private async Task<RepositoryResponse> IndexPrivateRepositoryAsync(HttpClient client, string? directory = null)
    {
        var repository = await RepositoryAsync(client, "private-repo");
        await database.IndexAsync(repository.Id, SampleRepo.Source(directory));
        repository = await RepositoryAsync(client, "private-repo");
        Assert.NotNull(repository.LatestSnapshot);
        return repository;
    }

    private static async Task<RepositoryResponse> RepositoryAsync(HttpClient client, string name) =>
        (await client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!.Single(r => r.Name == name);

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, Guid repositoryId, int issueNumber, Guid snapshotId, string? idempotencyKey = null) =>
        client.PostAsJsonAsync(
            $"/api/v1/repositories/{repositoryId}/issues/{issueNumber}/plans", new CreatePlanRequest(snapshotId, idempotencyKey));

    private static async Task<PlanResponse> WaitAsync(HttpClient client, Guid planId)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            var plan = await client.GetFromJsonAsync<PlanResponse>($"/api/v1/plans/{planId}");
            if (plan!.State is not ("queued" or "running"))
            {
                return plan;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The plan did not finish in time.");
    }

    /// <summary>Asserts a problem response and its <c>code</c>; returns the problem document.</summary>
    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(code, problem.RootElement.TryGetProperty("code", out var actual) ? actual.GetString() : null);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        return problem.RootElement.Clone();
    }

    /// <summary>
    /// The stored plan row without its two review columns, its reviewed time, and a digest of
    /// every other row a plan request could touch.
    /// </summary>
    private async Task<PlanFingerprint> FingerprintAsync(Guid planId)
    {
        await using var db = database.CreateContext();
        var row = await db.Database
            .SqlQueryRaw<string>("""SELECT (to_jsonb(p) - 'reviewed_at' - 'updated_at')::text AS "Value" FROM plans p WHERE p.id = {0}""", planId)
            .SingleAsync();
        var rest = await db.Database
            .SqlQueryRaw<string>(
                """
                SELECT concat_ws('|',
                    (SELECT md5(coalesce(string_agg(i::text, ',' ORDER BY i.id), '')) FROM investigations i),
                    (SELECT md5(coalesce(string_agg(e::text, ',' ORDER BY e.id), '')) FROM evidence_refs e),
                    (SELECT md5(coalesce(string_agg(v::text, ',' ORDER BY v.id), '')) FROM investigation_events v),
                    (SELECT md5(coalesce(string_agg(j::text, ',' ORDER BY j.id), '')) FROM jobs j),
                    (SELECT md5(coalesce(string_agg(r::text, ',' ORDER BY r.id), '')) FROM repositories r),
                    (SELECT md5(coalesce(string_agg(s::text, ',' ORDER BY s.id), '')) FROM index_snapshots s),
                    (SELECT md5(coalesce(string_agg(o::text, ',' ORDER BY o.id), '')) FROM plans o WHERE o.id <> {0})) AS "Value"
                """,
                planId)
            .SingleAsync();
        var reviewedAt = (await db.Plans.AsNoTracking().SingleAsync(p => p.Id == planId)).ReviewedAt;
        return new PlanFingerprint(row, rest, reviewedAt);
    }

    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task SignInAsync(HttpClient client, string user)
    {
        using var login = await client.GetAsync("/api/v1/auth/login");
        var state = login.Headers.Location!.Query.Split("state=")[1];
        using var callback = await client.GetAsync($"/api/v1/auth/callback?code={user}&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
    }

    private sealed record PlanFingerprint(string PlanWithoutReview, string EverythingElse, DateTimeOffset? ReviewedAt);

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Query);

    /// <summary>The chat model of the API under test: the deterministic fake unless a test puts another in its place.</summary>
    private sealed class SwitchableChatModel : IChatModel
    {
        public IChatModel Inner { get; set; } = new DeterministicFakeChatModel();

        public string ModelId => Inner.ModelId;

        public Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            Inner.CompleteAsync(request, cancellationToken);
    }

    private sealed class FakeIssue(string title, string body)
    {
        public string Title { get; set; } = title;

        public string Body { get; set; } = body;

        public string State { get; set; } = "open";

        public List<string> Labels { get; } = ["bug"];

        public List<string> Comments { get; } = [];
    }

    /// <summary>
    /// GitHub, as far as plans need it. "alice" and "bob" are sign-in codes and logins; alice can
    /// reach <c>acme/private-repo</c>. Issue calls are answered only for a token that was asked
    /// for the issues permission. Every request is recorded.
    /// </summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private const string IssuesToken = "issues-token";
        private const string RepoPath = "/repos/acme/private-repo";
        private static readonly Dictionary<string, long> UserIds = new() { ["alice"] = 1, ["bob"] = 2 };
        private readonly ConcurrentQueue<RecordedRequest> requests = new();

        public IReadOnlyCollection<RecordedRequest> Requests => requests;

        public ConcurrentQueue<JsonDocument> TokenRequests { get; } = new();

        public ConcurrentDictionary<int, FakeIssue> Issues { get; } = new();

        public bool GrantsIssuesPermission { get; set; } = true;

        /// <summary>Answers a request in place of the stub when it returns a response.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Enqueue(new RecordedRequest(request.Method, path, request.RequestUri.Query));
            var bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
            var user = bearer.StartsWith("user-token-", StringComparison.Ordinal) ? bearer["user-token-".Length..] : null;

            if (path == "/login/oauth/access_token")
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                var code = form.Split('&').Single(p => p.StartsWith("code=", StringComparison.Ordinal))[5..];
                return Json(new { access_token = UserIds.ContainsKey(code) ? $"user-token-{code}" : null });
            }

            if (user is not null && path == "/user")
            {
                return Json(new { id = UserIds[user], login = user });
            }

            if (user is not null && path == "/user/installations")
            {
                return Json(new { installations = new[] { new { id = InstallationId, account = new { login = "acme" } } } });
            }

            if (user is not null && path == $"/user/installations/{InstallationId}/repositories")
            {
                return Json(new
                {
                    repositories = user == "alice"
                        ? new object[] { new { id = PrivateRepoId, name = "private-repo", owner = new { login = "acme" }, default_branch = "main", @private = true } }
                        : [],
                });
            }

            if (path == $"/app/installations/{InstallationId}/access_tokens" && bearer.Count(c => c == '.') == 2)
            {
                var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                TokenRequests.Enqueue(document);
                var wantsIssues = document.RootElement.GetProperty("permissions").TryGetProperty("issues", out _);
                if (wantsIssues && !GrantsIssuesPermission)
                {
                    return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                    {
                        Content = new StringContent("""{"message":"The permissions requested are not granted to this installation."}"""),
                    };
                }

                return Json(new { token = wantsIssues ? IssuesToken : "contents-token" });
            }

            if (Override?.Invoke(request) is { } overridden)
            {
                return overridden;
            }

            if (!path.StartsWith(RepoPath, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path == RepoPath)
            {
                return Json(new { id = PrivateRepoId, name = "private-repo", has_issues = true });
            }

            if (bearer != IssuesToken)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"Resource not accessible by integration"}""") };
            }

            var day = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            var segments = path[RepoPath.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (segments)
            {
                case ["issues", var number] when Issues.TryGetValue(int.Parse(number), out var issue):
                    return Json(new Dictionary<string, object?>
                    {
                        ["id"] = 9_000_000L + int.Parse(number),
                        ["number"] = int.Parse(number),
                        ["title"] = issue.Title,
                        ["state"] = issue.State,
                        ["user"] = new { login = "reporter", id = 77 },
                        ["labels"] = issue.Labels.Select(label => new { id = 1, name = label }),
                        ["body"] = issue.Body,
                        ["comments"] = issue.Comments.Count,
                        ["created_at"] = day,
                        ["updated_at"] = day,
                    });

                case ["issues", var number, "comments"] when Issues.TryGetValue(int.Parse(number), out var issue):
                    return Json(issue.Comments.Select((body, index) => new
                    {
                        id = 1000L + index,
                        user = new { login = "commenter" },
                        body,
                        created_at = day.AddMinutes(index),
                    }));

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") };
            }
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
