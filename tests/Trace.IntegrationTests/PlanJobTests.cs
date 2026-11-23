using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trace.Application.Investigations;
using Trace.Application.Issues;
using Trace.Application.Jobs;
using Trace.Application.Plans;
using Trace.Application.Telemetry;
using Trace.Domain;
using Trace.Infrastructure.Persistence;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>
/// Plan investigations as the worker runs them: through the durable queue, with the services
/// the application registers. The first tests use the deterministic fake model and the fixture
/// issues, exactly as the local demo does; no real model and no real issue is involved.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PlanJobTests(PostgresFixture postgres)
{
    private const string TaskRepositoryPath = "src/TaskBoard.Api/Tasks/TaskRepository.cs";
    private const string InjectionMarker = "TRACE-INJECTION-MARKER-7F3A";
    private static readonly TimeSpan PastLease = TimeSpan.FromSeconds(31);
    private static readonly (string Path, int StartLine, int EndLine) Bug = (TaskRepositoryPath, 24, 26);

    private readonly ManualTimeProvider clock = new();

    private static RawIssueThread PagingIssue =>
        StubIssues.Issue(3, "First page of the task list skips the oldest tasks", "Page 1 returns tasks 21 to 25.");

    [Fact]
    public async Task A_plan_for_a_fixture_issue_runs_as_a_job_with_the_fake_model_and_stores_a_validated_plan()
    {
        await using var database = await CreateAsync();

        var queued = await RequestAsync(database, issueNumber: 3);

        // Requesting only queues: nothing has been read, asked or planned yet.
        Assert.Equal(InvestigationState.Queued, queued.Investigation.Investigation.State);
        Assert.Empty(queued.Investigation.Events);
        Assert.Equal((false, null), (queued.Plan.IsFinished, queued.Plan.IssueTitle));
        var job = await database.GetJobAsync(queued.Investigation.JobId!.Value);
        Assert.Equal((JobStatus.Queued, "plan.run"), (job.Status, job.Kind));

        Assert.True(await database.ProcessNextJobAsync());
        Assert.False(await database.ProcessNextJobAsync());

        var record = await GetAsync(database, queued.Investigation.Investigation.Id);
        var investigation = record.Investigation.Investigation;
        Assert.Equal(InvestigationState.Completed, investigation.State);
        Assert.Equal(("fake-extractive-v1", PlanPrompt.Version), (investigation.ModelId, investigation.PromptVersion));
        Assert.Equal(JobStatus.Succeeded, (await database.GetJobAsync(job.Id)).Status);
        Assert.Equal(
            [
                (InvestigationEventType.Started, null),
                (InvestigationEventType.ToolCalled, "search_code"),
                (InvestigationEventType.Completed, null),
            ],
            record.Investigation.Events.Select(e => (e.EventType, e.ToolName)));

        // The issue the plan was made from is the fixture's, as the issue reader bounds it.
        var issue = await ReadFixtureIssueAsync(database, investigation.RepositoryId, 3);
        Assert.Equal("First page of the task list skips the oldest tasks", record.Plan.IssueTitle);
        Assert.Equal(IssueContentHash.Compute(issue), record.Plan.IssueContentHash);

        // A stored plan that passes the strict reader, with nothing removed by validation: the
        // fake model cites only what its search returned.
        Assert.True(record.Plan.IsFinished);
        var result = PlanResult.FromJson(record.Plan.ResultJson!);
        Assert.Equal(PlanOutcome.Planned, result.Outcome);
        Assert.InRange(result.Steps.Count, 1, 3);
        Assert.NotEmpty(result.FilesToChange);
        Assert.All(result.FilesToChange, file => Assert.Equal(PlanFileChange.Modify, file.Change));
        Assert.Single(result.Risks);
        Assert.Single(result.Verification);
        Assert.Contains("no language model read this issue", result.Summary.Text, StringComparison.Ordinal);
        Assert.Equal(PlanTrimmed.Nothing, PlanRunSummary.FromJson(investigation.ResultJson!).Plan.Trimmed);

        // Every citation is real: the excerpt is the snapshot's text at that location, and the
        // ID is an evidence row of this investigation.
        Assert.InRange(result.Evidence.Count, 1, 3);
        Assert.All(result.Evidence, evidence => Assert.Equal(
            string.Join('\n', SampleRepo.ReadLines(evidence.Path)[(evidence.StartLine - 1)..evidence.EndLine]),
            evidence.Excerpt));
        await using var db = database.CreateContext();
        Assert.Equal(
            result.Evidence.Select(e => e.Id).Order(),
            (await db.EvidenceRefs.Where(e => e.InvestigationId == investigation.Id).Select(e => e.Id).ToListAsync()).Order());

        // Deterministic: the same issue on the same snapshot gives the same plan again.
        var second = await RequestAsync(database, issueNumber: 3);
        Assert.True(await database.ProcessNextJobAsync());
        var again = PlanResult.FromJson((await GetAsync(database, second.Investigation.Investigation.Id)).Plan.ResultJson!);
        Assert.Equal(result.Steps.Select(step => step.Text), again.Steps.Select(step => step.Text));
        Assert.Equal(
            result.Evidence.Select(e => (e.Path, e.StartLine, e.EndLine, e.Excerpt)),
            again.Evidence.Select(e => (e.Path, e.StartLine, e.EndLine, e.Excerpt)));
        Assert.Equal(2, await db.Plans.CountAsync());
    }

    [Fact]
    public async Task A_plan_run_on_the_injection_issue_leaves_no_issue_plan_or_source_text_in_spans_or_logs()
    {
        var logs = new CapturedLogs();
        await using var database = await CreateAsync(logs: logs);

        // Migrating and indexing the fixture are not part of the run under test.
        logs.Clear();
        var correlationId = $"test-{Guid.NewGuid():N}";
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TraceTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        PlanRecord queued;
        using (var request = TraceTelemetry.Source.StartActivity("test request"))
        {
            TraceTelemetry.SetCorrelationId(request, correlationId);
            queued = await RequestAsync(database, issueNumber: 14);
        }

        Assert.True(await database.ProcessNextJobAsync());

        var record = await GetAsync(database, queued.Investigation.Investigation.Id);
        var investigation = record.Investigation.Investigation;
        Assert.Equal(InvestigationState.Completed, investigation.State);
        Assert.StartsWith("SYNTHETIC TEST ISSUE", record.Plan.IssueTitle, StringComparison.Ordinal);

        // The injected instructions changed nothing: one search, no refused tool, no plan text from the issue.
        Assert.DoesNotContain(record.Investigation.Events, e => e.EventType is InvestigationEventType.ToolRejected or InvestigationEventType.ToolFailed);
        Assert.Equal("search_code", Assert.Single(record.Investigation.Events, e => e.EventType == InvestigationEventType.ToolCalled).ToolName);
        var result = PlanResult.FromJson(record.Plan.ResultJson!);
        foreach (var injected in new[] { InjectionMarker, "delete_repository", "run_shell", "MasterKey", "maintenance mode", "other-private-repo" })
        {
            Assert.DoesNotContain(injected, record.Plan.ResultJson!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(injected, investigation.ResultJson!, StringComparison.OrdinalIgnoreCase);
        }

        // One trace covers the request, the job, reading the issue, the model and the tool.
        var ours = spans.Where(span => span.GetBaggageItem(TraceTelemetry.CorrelationKey) == correlationId).ToList();
        foreach (var name in new[] { "job plan.run", "investigation.run", "issues.get", "model.turn", "tool search_code" })
        {
            Assert.Contains(ours, span => span.OperationName == name);
        }

        var run = ours.Single(span => span.OperationName == "investigation.run");
        Assert.Equal(result.Outcome == PlanOutcome.Planned ? "planned" : "insufficient_evidence", run.GetTagItem("trace.investigation.outcome"));
        Assert.Equal(14, run.GetTagItem("trace.issue.number"));
        Assert.Equal(result.Steps.Count, run.GetTagItem("trace.plan.steps"));
        Assert.Equal(result.Evidence.Count, run.GetTagItem("trace.investigation.evidence"));
        Assert.Equal("succeeded", ours.Single(span => span.OperationName == "job plan.run").GetTagItem("trace.job.outcome"));
        Assert.Equal("ok", ours.Single(span => span.OperationName == "issues.get").GetTagItem("trace.issues.outcome"));

        // Spans and logs carry identifiers, counts, durations and fixed names: never text of the
        // issue, of the plan or of the source.
        var recorded = ours
            .SelectMany(span => span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
                .Concat(span.Events.Select(e => e.Name))
                .Concat(span.Baggage.Select(item => $"{item.Key}={item.Value}"))
                .Append(span.DisplayName)
                .Append(span.StatusDescription ?? string.Empty))
            .ToList();
        Assert.NotEmpty(logs.Lines);
        Assert.Contains(logs.Lines, line => line.Contains(queued.Investigation.JobId!.Value.ToString(), StringComparison.Ordinal));

        var forbidden = new List<string>
        {
            InjectionMarker, "SYNTHETIC", "prompt-injection", "maintenance mode", "delete_repository", "attacker.invalid",
            "MasterKey", "title limit", "Synthetic test comment",
            "Extractive test model", "no language model", "match the wording", "was not assessed",
            "TaskBoard", "docs/", ".cs", ".md",
        };
        forbidden.AddRange(result.Steps.Select(step => step.Text));
        forbidden.AddRange(result.Evidence.Select(evidence => evidence.Path));
        forbidden.AddRange(result.Evidence
            .SelectMany(evidence => evidence.Excerpt.Split('\n'))
            .Select(line => line.Trim())
            .Where(line => line.Length >= 12));
        Assert.True(forbidden.Count > 20);
        foreach (var text in forbidden)
        {
            var inSpan = recorded.FirstOrDefault(entry => entry.Contains(text, StringComparison.OrdinalIgnoreCase));
            Assert.True(inSpan is null, $"A span carries '{text}': {inSpan}");
            var inLog = logs.Lines.FirstOrDefault(line => line.Contains(text, StringComparison.OrdinalIgnoreCase));
            Assert.True(inLog is null, $"A log line carries '{text}': {inLog}");
        }
    }

    [Fact]
    public async Task Model_outage_is_retried_and_the_plan_is_then_made_once()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => throw new HttpRequestException("503"),
            _ => throw new HttpRequestException("503"),
            _ => ModelTurn.Call(model.Read(TaskRepositoryPath, 20, 29)),
            _ => Planned());
        var issues = new StubIssues { Thread = PagingIssue };
        await using var database = await CreateAsync(model, issues);
        var queued = await RequestAsync(database);
        var id = queued.Investigation.Investigation.Id;

        Assert.True(await database.ProcessNextJobAsync());
        var interrupted = await GetAsync(database, id);
        Assert.Equal((InvestigationState.Running, false), (interrupted.Investigation.Investigation.State, interrupted.Plan.IsFinished));
        Assert.Equal(PagingIssue.Issue.Title, interrupted.Plan.IssueTitle);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await database.ProcessNextJobAsync());
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await database.ProcessNextJobAsync());

        var finished = await GetAsync(database, id);
        Assert.Equal(InvestigationState.Completed, finished.Investigation.Investigation.State);
        Assert.Equal(PlanOutcome.Planned, PlanResult.FromJson(finished.Plan.ResultJson!).Outcome);
        Assert.Equal(
            [
                InvestigationEventType.Started, InvestigationEventType.Started, InvestigationEventType.Started,
                InvestigationEventType.ToolCalled, InvestigationEventType.Completed,
            ],
            finished.Investigation.Events.Select(e => e.EventType));
        Assert.Equal([1, 2, 3, 4, 5], finished.Investigation.Events.Select(e => e.Seq));
        Assert.Equal(3, issues.Fetches);
        var job = await database.GetJobAsync(queued.Investigation.JobId!.Value);
        Assert.Equal((JobStatus.Succeeded, 3), (job.Status, job.Attempts));

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Plans.CountAsync());
        Assert.Equal(1, await db.EvidenceRefs.CountAsync(e => e.InvestigationId == id));
        Assert.False(await database.ProcessNextJobAsync());
    }

    [Fact]
    public async Task Model_outage_on_every_attempt_fails_the_investigation_and_leaves_the_plan_unfinished()
    {
        var model = new ScriptedChatModel(_ => throw new HttpRequestException("503"));
        await using var database = await CreateAsync(model, new StubIssues { Thread = PagingIssue });
        var queued = await RequestAsync(database);

        for (var attempt = 0; attempt < Job.DefaultMaxAttempts; attempt++)
        {
            Assert.True(await database.ProcessNextJobAsync());
            clock.Advance(TimeSpan.FromMinutes(2));
        }

        var failed = await GetAsync(database, queued.Investigation.Investigation.Id);
        Assert.Equal((InvestigationState.Failed, "model_error"), (failed.Investigation.Investigation.State, failed.Investigation.Investigation.ErrorCode));
        Assert.Equal(InvestigationEventType.Failed, failed.Investigation.Events[^1].EventType);
        Assert.Equal((false, PagingIssue.Issue.Title), (failed.Plan.IsFinished, failed.Plan.IssueTitle));
        Assert.Equal(Job.DefaultMaxAttempts, model.Requests.Count);
        Assert.False(await database.ProcessNextJobAsync());
    }

    [Fact]
    public async Task GitHub_being_down_is_retried_by_the_queue_and_the_run_fails_with_a_fixed_name_when_it_stays_down()
    {
        var model = new ScriptedChatModel(_ => Planned());
        var issues = new StubIssues { Thread = PagingIssue, Failure = _ => IssueReadError.GitHubUnavailable };
        await using var database = await CreateAsync(model, issues);
        var queued = await RequestAsync(database);

        for (var attempt = 0; attempt < Job.DefaultMaxAttempts; attempt++)
        {
            Assert.True(await database.ProcessNextJobAsync());
            clock.Advance(TimeSpan.FromMinutes(2));
        }

        var failed = await GetAsync(database, queued.Investigation.Investigation.Id);
        Assert.Equal((InvestigationState.Failed, "github_unavailable"), (failed.Investigation.Investigation.State, failed.Investigation.Investigation.ErrorCode));
        Assert.Equal(Job.DefaultMaxAttempts, issues.Fetches);
        Assert.Empty(model.Requests);
        Assert.False(failed.Plan.IsFinished);

        // The run recorded its own end, so the job is done rather than failed.
        Assert.Equal(JobStatus.Succeeded, (await database.GetJobAsync(queued.Investigation.JobId!.Value)).Status);
    }

    [Fact]
    public async Task Plan_run_interrupted_by_a_dead_worker_is_restarted_and_finishes_once()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => ModelTurn.Call(model.Search("task list first page")),
            _ => throw new OperationCanceledException("the worker process was killed here"),
            _ => ModelTurn.Call(model.Read(TaskRepositoryPath, 20, 29)),
            _ => Planned());
        var issues = new StubIssues { Thread = PagingIssue };
        await using var database = await CreateAsync(model, issues);
        var queued = await RequestAsync(database);
        var id = queued.Investigation.Investigation.Id;

        // First worker: claims the job, gets part of the way, and dies without reporting anything.
        Assert.NotNull(await database.WithQueueAsync(queue => queue.ClaimNextAsync(TimeSpan.FromSeconds(30))));
        await using (var scope = database.CreateScope())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider
                .GetRequiredService<PlanInvestigator>()
                .RunAsync(id, attempt: 1, maxAttempts: 3, CancellationToken.None));
        }

        var interrupted = await GetAsync(database, id);
        Assert.Equal((InvestigationState.Running, false), (interrupted.Investigation.Investigation.State, interrupted.Plan.IsFinished));
        Assert.False(await database.ProcessNextJobAsync());

        // Its lease runs out and a second worker takes over.
        clock.Advance(PastLease);
        Assert.True(await database.ProcessNextJobAsync());

        var finished = await GetAsync(database, id);
        Assert.Equal(InvestigationState.Completed, finished.Investigation.Investigation.State);
        Assert.True(finished.Plan.IsFinished);
        Assert.Equal(
            [
                InvestigationEventType.Started,
                InvestigationEventType.ToolCalled,
                InvestigationEventType.Started,
                InvestigationEventType.ToolCalled,
                InvestigationEventType.Completed,
            ],
            finished.Investigation.Events.Select(e => e.EventType));
        Assert.Equal(2, issues.Fetches);

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Plans.CountAsync());
        Assert.Equal(1, await db.EvidenceRefs.CountAsync(e => e.InvestigationId == id));
        var job = await database.GetJobAsync(queued.Investigation.JobId!.Value);
        Assert.Equal((JobStatus.Succeeded, 2), (job.Status, job.Attempts));
    }

    [Fact]
    public async Task Plan_run_whose_worker_dies_every_time_is_failed_and_its_plan_stays_unfinished()
    {
        var model = new ScriptedChatModel(_ => Planned());
        await using var database = await CreateAsync(model, new StubIssues { Thread = PagingIssue });
        var queued = await RequestAsync(database);

        for (var attempt = 0; attempt < Job.DefaultMaxAttempts; attempt++)
        {
            Assert.NotNull(await database.WithQueueAsync(queue => queue.ClaimNextAsync(TimeSpan.FromSeconds(30))));
            clock.Advance(PastLease);
        }

        Assert.False(await database.ProcessNextJobAsync());

        var failed = await GetAsync(database, queued.Investigation.Investigation.Id);
        Assert.Equal((InvestigationState.Failed, "worker_interrupted"), (failed.Investigation.Investigation.State, failed.Investigation.Investigation.ErrorCode));
        Assert.False(failed.Plan.IsFinished);
        Assert.Equal(JobStatus.Failed, (await database.GetJobAsync(queued.Investigation.JobId!.Value)).Status);
    }

    [Fact]
    public async Task Cancelling_a_running_plan_stops_it_and_leaves_an_unfinished_plan_that_says_which_issue_it_was_about()
    {
        var modelCalled = new TaskCompletionSource();
        var issues = new StubIssues { Thread = PagingIssue };
        await using var database = await CreateAsync(new BlockingChatModel(modelCalled), issues);
        var queued = await RequestAsync(database);
        var id = queued.Investigation.Investigation.Id;

        var processing = database.ProcessNextJobAsync();
        await modelCalled.Task;
        var cancelRequested = await CancelAsync(database, queued.Investigation.JobId!.Value);
        Assert.True(await processing);

        Assert.NotNull(cancelRequested!.RequestedCancelAt);
        var cancelled = await GetAsync(database, id);
        Assert.Equal(InvestigationState.Cancelled, cancelled.Investigation.Investigation.State);
        Assert.Null(cancelled.Investigation.Investigation.ResultJson);
        Assert.Equal(InvestigationEventType.Cancelled, cancelled.Investigation.Events[^1].EventType);
        Assert.Equal(JobStatus.Cancelled, (await database.GetJobAsync(queued.Investigation.JobId.Value)).Status);

        // Consistent: no result, no schema version, no evidence; the issue it read is still named.
        Assert.False(cancelled.Plan.IsFinished);
        Assert.Null(cancelled.Plan.SchemaVersion);
        Assert.Equal((3, PagingIssue.Issue.Title), (cancelled.Plan.IssueNumber, cancelled.Plan.IssueTitle));
        Assert.Equal(IssueContentHash.Compute(IssueReader.Bound(PagingIssue)), cancelled.Plan.IssueContentHash);
        await using var db = database.CreateContext();
        Assert.Equal(0, await db.EvidenceRefs.CountAsync(e => e.InvestigationId == id));
        Assert.False(await database.ProcessNextJobAsync());
    }

    [Fact]
    public async Task Cancelling_before_a_worker_starts_means_no_issue_is_read_and_the_model_is_never_called()
    {
        var model = new ScriptedChatModel(_ => Planned());
        var issues = new StubIssues { Thread = PagingIssue };
        await using var database = await CreateAsync(model, issues);
        var queued = await RequestAsync(database);

        var job = await CancelAsync(database, queued.Investigation.JobId!.Value);
        Assert.False(await database.ProcessNextJobAsync());

        Assert.Equal(JobStatus.Cancelled, job!.Status);
        var cancelled = await GetAsync(database, queued.Investigation.Investigation.Id);
        Assert.Equal(InvestigationState.Cancelled, cancelled.Investigation.Investigation.State);
        Assert.Equal((false, null, null), (cancelled.Plan.IsFinished, cancelled.Plan.IssueTitle, cancelled.Plan.IssueContentHash));
        Assert.Equal(0, issues.Fetches);
        Assert.Empty(model.Requests);
    }

    private static ModelTurn Planned() =>
        Proposal.Turn(
            summary: Proposal.Statement("Skip (Page - 1) pages in List.", Bug),
            steps: [Proposal.Statement("Skip (Page - 1) * PageSize items in List.", Bug)],
            files: [Proposal.File(TaskRepositoryPath, "modify", "The skip is computed here.", Bug)]);

    /// <summary>
    /// A database holding the indexed demo repository. Without a model the application's
    /// deterministic fake is used; without issues, the fixture issues the demo repository serves.
    /// </summary>
    private async Task<TestDatabase> CreateAsync(IChatModel? model = null, StubIssues? issues = null, CapturedLogs? logs = null)
    {
        var database = await postgres.CreateDatabaseAsync(
            configureServices: services =>
            {
                services.AddSingleton<TimeProvider>(clock).AddSingleton(JobTestExtensions.FastOptions());
                if (model is not null)
                {
                    services.AddSingleton(model);
                }

                if (issues is not null)
                {
                    services.AddScoped<IIssueSourceResolver>(_ => issues);
                }

                if (logs is not null)
                {
                    services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
                }
            },
            settings: new Dictionary<string, string?>
            {
                ["Demo:FixturePath"] = SampleRepo.Path,
                ["Demo:IssuesPath"] = SampleRepo.Path + ".issues",
            });

        await using var db = database.CreateContext();
        var repository = Repository.Create(DemoData.RepositoryOwner, DemoData.RepositoryName, "main", RepositoryVisibility.Public);
        db.Add(repository);
        await db.SaveChangesAsync();
        await database.IndexAsync(repository.Id, SampleRepo.Source());
        return database;
    }

    private static async Task<PlanRecord> RequestAsync(TestDatabase database, int issueNumber = 3)
    {
        await using var db = database.CreateContext();
        var snapshot = await db.IndexSnapshots.SingleAsync(s => s.Status == SnapshotStatus.Completed);

        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlanInvestigator>()
            .CreateAsync(new RequestPlan(snapshot.RepositoryId, snapshot.Id, issueNumber));
    }

    private static async Task<PlanRecord> GetAsync(TestDatabase database, Guid investigationId)
    {
        await using var scope = database.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<PlanInvestigator>().GetAsync(investigationId))!;
    }

    private static async Task<IssueDetail> ReadFixtureIssueAsync(TestDatabase database, Guid repositoryId, int number)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IssueReader>().GetAsync(repositoryId, number);
    }

    private static async Task<Job?> CancelAsync(TestDatabase database, Guid jobId)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JobService>().CancelAsync(jobId);
    }
}
