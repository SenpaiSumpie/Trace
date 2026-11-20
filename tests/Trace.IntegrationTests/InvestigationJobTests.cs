using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trace.Application.Indexing;
using Trace.Application.Investigations;
using Trace.Application.Jobs;
using Trace.Domain;
using Trace.Infrastructure.Persistence;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>Investigations and indexing as the worker runs them: through the durable queue.</summary>
[Collection(PostgresCollection.Name)]
public class InvestigationJobTests(PostgresFixture postgres)
{
    private const string TaskRepositoryPath = "src/TaskBoard.Api/Tasks/TaskRepository.cs";
    private static readonly TimeSpan PastLease = TimeSpan.FromSeconds(31);

    private readonly ManualTimeProvider clock = new();

    [Fact]
    public async Task Asking_only_queues_the_work_and_a_worker_completes_it()
    {
        var model = new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence());
        await using var database = await CreateAsync(model);

        var queued = await AskAsync(database);

        Assert.Equal(InvestigationState.Queued, queued.Investigation.State);
        Assert.Empty(queued.Events);
        Assert.Empty(model.Requests);
        var job = await database.GetJobAsync(queued.JobId!.Value);
        Assert.Equal((JobStatus.Queued, InvestigationJobHandler.JobKind), (job.Status, job.Kind));
        Assert.Equal(queued.Investigation.RepositoryId, job.ScopedRepositoryId);

        Assert.True(await database.ProcessNextJobAsync());

        var finished = await GetAsync(database, queued.Investigation.Id);
        Assert.Equal(InvestigationState.Completed, finished.Investigation.State);
        Assert.Equal(JobStatus.Succeeded, (await database.GetJobAsync(job.Id)).Status);
    }

    [Fact]
    public async Task Model_outage_is_retried_and_the_investigation_then_completes()
    {
        var model = new ScriptedChatModel(
            _ => throw new HttpRequestException("503"),
            _ => throw new HttpRequestException("503"),
            _ => ScriptedChatModel.NotEnoughEvidence());
        await using var database = await CreateAsync(model);
        var queued = await AskAsync(database);

        Assert.True(await database.ProcessNextJobAsync());
        Assert.Equal(InvestigationState.Running, (await GetAsync(database, queued.Investigation.Id)).Investigation.State);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await database.ProcessNextJobAsync());
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await database.ProcessNextJobAsync());

        var finished = await GetAsync(database, queued.Investigation.Id);
        Assert.Equal(InvestigationState.Completed, finished.Investigation.State);
        Assert.Null(finished.Investigation.ErrorCode);
        Assert.Equal(
            [InvestigationEventType.Started, InvestigationEventType.Started, InvestigationEventType.Started, InvestigationEventType.Completed],
            finished.Events.Select(e => e.EventType));
        Assert.Equal([1, 2, 3, 4], finished.Events.Select(e => e.Seq));
        Assert.Contains("attempt 3", finished.Events[2].Summary, StringComparison.Ordinal);
        Assert.Equal((JobStatus.Succeeded, 3), ((await database.GetJobAsync(queued.JobId!.Value)).Status, (await database.GetJobAsync(queued.JobId.Value)).Attempts));
    }

    [Fact]
    public async Task Model_outage_on_every_attempt_fails_the_investigation_with_a_reason()
    {
        var model = new ScriptedChatModel(_ => throw new HttpRequestException("503"));
        await using var database = await CreateAsync(model);
        var queued = await AskAsync(database);

        for (var attempt = 0; attempt < Job.DefaultMaxAttempts; attempt++)
        {
            Assert.True(await database.ProcessNextJobAsync());
            clock.Advance(TimeSpan.FromMinutes(2));
        }

        var failed = await GetAsync(database, queued.Investigation.Id);
        Assert.Equal((InvestigationState.Failed, "model_error"), (failed.Investigation.State, failed.Investigation.ErrorCode));
        Assert.Equal(InvestigationEventType.Failed, failed.Events[^1].EventType);
        Assert.Equal(Job.DefaultMaxAttempts, model.Requests.Count);
        Assert.False(await database.ProcessNextJobAsync());
    }

    [Fact]
    public async Task Investigation_interrupted_by_a_dead_worker_is_restarted_and_finishes_once()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => ModelTurn.Call(model.Search("task list first page")),
            _ => throw new OperationCanceledException("the worker process was killed here"),
            _ => ModelTurn.Call(model.Read(TaskRepositoryPath, 20, 29)),
            _ => ScriptedChatModel.Answer("Paging skips a page.", (TaskRepositoryPath, 24, 26)));
        await using var database = await CreateAsync(model);
        var queued = await AskAsync(database);

        // First worker: claims the job, gets part of the way, and dies without reporting anything.
        Assert.NotNull(await database.WithQueueAsync(queue => queue.ClaimNextAsync(TimeSpan.FromSeconds(30))));
        await using (var scope = database.CreateScope())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider
                .GetRequiredService<QuestionInvestigator>()
                .RunAsync(queued.Investigation.Id, attempt: 1, maxAttempts: 3, CancellationToken.None));
        }

        Assert.Equal(InvestigationState.Running, (await GetAsync(database, queued.Investigation.Id)).Investigation.State);
        Assert.False(await database.ProcessNextJobAsync());

        // Its lease runs out and a second worker takes over.
        clock.Advance(PastLease);
        Assert.True(await database.ProcessNextJobAsync());

        var finished = await GetAsync(database, queued.Investigation.Id);
        Assert.Equal(InvestigationState.Completed, finished.Investigation.State);
        Assert.Equal(InvestigationOutcome.Answered, InvestigationResult.FromJson(finished.Investigation.ResultJson!).Outcome);
        Assert.Equal(
            [
                InvestigationEventType.Started,
                InvestigationEventType.ToolCalled,
                InvestigationEventType.Started,
                InvestigationEventType.ToolCalled,
                InvestigationEventType.Completed,
            ],
            finished.Events.Select(e => e.EventType));
        Assert.Equal([1, 2, 3, 4, 5], finished.Events.Select(e => e.Seq));

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.EvidenceRefs.CountAsync(e => e.InvestigationId == queued.Investigation.Id));
        Assert.Equal((JobStatus.Succeeded, 2), ((await database.GetJobAsync(queued.JobId!.Value)).Status, (await database.GetJobAsync(queued.JobId.Value)).Attempts));
    }

    [Fact]
    public async Task Investigation_whose_worker_dies_every_time_is_failed_rather_than_left_running()
    {
        var model = new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence());
        await using var database = await CreateAsync(model);
        var queued = await AskAsync(database);

        for (var attempt = 0; attempt < Job.DefaultMaxAttempts; attempt++)
        {
            Assert.NotNull(await database.WithQueueAsync(queue => queue.ClaimNextAsync(TimeSpan.FromSeconds(30))));
            clock.Advance(PastLease);
        }

        Assert.False(await database.ProcessNextJobAsync());

        var failed = await GetAsync(database, queued.Investigation.Id);
        Assert.Equal((InvestigationState.Failed, "worker_interrupted"), (failed.Investigation.State, failed.Investigation.ErrorCode));
        Assert.Equal(InvestigationEventType.Failed, failed.Events[^1].EventType);
        Assert.Equal(JobStatus.Failed, (await database.GetJobAsync(queued.JobId!.Value)).Status);
    }

    [Fact]
    public async Task Cancelling_a_running_investigation_stops_it()
    {
        var modelCalled = new TaskCompletionSource();
        var model = new BlockingChatModel(modelCalled);
        await using var database = await CreateAsync(model);
        var queued = await AskAsync(database);

        var processing = database.ProcessNextJobAsync();
        await modelCalled.Task;
        var cancelRequested = await CancelAsync(database, queued.JobId!.Value);
        Assert.True(await processing);

        Assert.True(cancelRequested!.RequestedCancelAt is not null);
        var cancelled = await GetAsync(database, queued.Investigation.Id);
        Assert.Equal(InvestigationState.Cancelled, cancelled.Investigation.State);
        Assert.Null(cancelled.Investigation.ResultJson);
        Assert.Equal(InvestigationEventType.Cancelled, cancelled.Events[^1].EventType);
        Assert.Equal(JobStatus.Cancelled, (await database.GetJobAsync(queued.JobId.Value)).Status);
    }

    [Fact]
    public async Task Cancelling_before_a_worker_starts_means_the_model_is_never_called()
    {
        var model = new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence());
        await using var database = await CreateAsync(model);
        var queued = await AskAsync(database);

        var job = await CancelAsync(database, queued.JobId!.Value);
        Assert.False(await database.ProcessNextJobAsync());

        Assert.Equal(JobStatus.Cancelled, job!.Status);
        Assert.Equal(InvestigationState.Cancelled, (await GetAsync(database, queued.Investigation.Id)).Investigation.State);
        Assert.Empty(model.Requests);

        // Cancelling again is harmless.
        Assert.Equal(JobStatus.Cancelled, (await CancelAsync(database, queued.JobId.Value))!.Status);
    }

    [Fact]
    public async Task Index_job_builds_and_publishes_a_snapshot()
    {
        await using var database = await CreateAsync(new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence()), indexFixture: false);
        var repositoryId = await DemoRepositoryIdAsync(database);

        var job = await EnqueueIndexAsync(database, repositoryId, "reindex-1");
        var sameRequest = await EnqueueIndexAsync(database, repositoryId, "reindex-1");
        Assert.Equal(job.Id, sameRequest.Id);

        Assert.True(await database.ProcessNextJobAsync());
        Assert.False(await database.ProcessNextJobAsync());

        await using var db = database.CreateContext();
        var snapshot = await db.IndexSnapshots.SingleAsync();
        Assert.Equal((SnapshotStatus.Completed, job.Id), (snapshot.Status, snapshot.BuildingJobId));
        Assert.True(await db.SourceChunks.AnyAsync(chunk => chunk.SnapshotId == snapshot.Id));
        Assert.Equal(JobStatus.Succeeded, (await database.GetJobAsync(job.Id)).Status);
    }

    [Fact]
    public async Task Index_job_clears_a_build_abandoned_by_a_dead_worker_so_the_revision_can_be_indexed()
    {
        await using var database = await CreateAsync(new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence()), indexFixture: false);
        var repositoryId = await DemoRepositoryIdAsync(database);

        // A build left behind by a job that no longer exists: it would block this revision forever.
        Guid abandonedId;
        await using (var db = database.CreateContext())
        {
            var revision = (await SampleRepo.Source().ReadAsync()).CommitSha;
            var abandoned = IndexSnapshot.Start(repositoryId, revision, "main", LineChunker.Version, "fake-hash-v1", buildingJobId: Guid.NewGuid());
            db.Add(abandoned);
            await db.SaveChangesAsync();
            abandonedId = abandoned.Id;
        }

        var job = await EnqueueIndexAsync(database, repositoryId, idempotencyKey: null);
        Assert.True(await database.ProcessNextJobAsync());

        await using var check = database.CreateContext();
        Assert.Equal(SnapshotStatus.Failed, (await check.IndexSnapshots.SingleAsync(s => s.Id == abandonedId)).Status);
        Assert.Equal(job.Id, (await check.IndexSnapshots.SingleAsync(s => s.Status == SnapshotStatus.Completed)).BuildingJobId);
    }

    [Fact]
    public async Task Index_job_leaves_a_build_alone_while_its_job_is_still_running()
    {
        await using var database = await CreateAsync(new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence()), indexFixture: false);
        var repositoryId = await DemoRepositoryIdAsync(database);

        // Another worker is indexing right now: it holds a live lease.
        var other = await EnqueueIndexAsync(database, repositoryId, "other-worker");
        Assert.NotNull(await database.WithQueueAsync(queue => queue.ClaimNextAsync(TimeSpan.FromSeconds(30))));
        Guid inProgressId;
        await using (var db = database.CreateContext())
        {
            var revision = (await SampleRepo.Source().ReadAsync()).CommitSha;
            var inProgress = IndexSnapshot.Start(repositoryId, revision, "main", LineChunker.Version, "fake-hash-v1", buildingJobId: other.Id);
            db.Add(inProgress);
            await db.SaveChangesAsync();
            inProgressId = inProgress.Id;
        }

        var second = await EnqueueIndexAsync(database, repositoryId, "second-request");
        Assert.True(await database.ProcessNextJobAsync());

        await using var check = database.CreateContext();
        Assert.Equal(SnapshotStatus.Building, (await check.IndexSnapshots.SingleAsync()).Status);
        Assert.Equal(inProgressId, (await check.IndexSnapshots.SingleAsync()).Id);
        Assert.Equal(JobStatus.Succeeded, (await database.GetJobAsync(second.Id)).Status);
    }

    [Fact]
    public async Task Index_job_cannot_be_requested_for_a_repository_without_a_source_or_one_that_is_not_visible()
    {
        await using var database = await CreateAsync(new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence()), indexFixture: false);
        await using var db = database.CreateContext();
        var noSource = await TestGraph.AddRepositoryAsync(db, "no-source");
        var hidden = Repository.Create("acme", "hidden", "main", RepositoryVisibility.Private);
        db.Add(hidden);
        await db.SaveChangesAsync();

        var withoutSource = await Assert.ThrowsAsync<JobRequestException>(() => EnqueueIndexAsync(database, noSource.Id, null));
        var notVisible = await Assert.ThrowsAsync<JobRequestException>(() => EnqueueIndexAsync(database, hidden.Id, null));

        Assert.Equal(JobRequestError.NoSource, withoutSource.Error);
        Assert.Equal(JobRequestError.RepositoryNotFound, notVisible.Error);
        Assert.Equal(0, await db.Jobs.CountAsync());
    }

    private async Task<TestDatabase> CreateAsync(IChatModel model, bool indexFixture = true)
    {
        var database = await postgres.CreateDatabaseAsync(
            configureServices: services => services
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton(JobTestExtensions.FastOptions())
                .AddSingleton(model),
            settings: new Dictionary<string, string?> { ["Demo:FixturePath"] = SampleRepo.Path });

        await using var db = database.CreateContext();
        var repository = Repository.Create(DemoData.RepositoryOwner, DemoData.RepositoryName, "main", RepositoryVisibility.Public);
        db.Add(repository);
        await db.SaveChangesAsync();
        if (indexFixture)
        {
            await database.IndexAsync(repository.Id, SampleRepo.Source());
        }

        return database;
    }

    private static async Task<Guid> DemoRepositoryIdAsync(TestDatabase database)
    {
        await using var db = database.CreateContext();
        return (await db.Repositories.SingleAsync(r => r.Name == DemoData.RepositoryName)).Id;
    }

    private static async Task<InvestigationRecord> AskAsync(TestDatabase database)
    {
        await using var db = database.CreateContext();
        var snapshot = await db.IndexSnapshots.SingleAsync(s => s.Status == SnapshotStatus.Completed);

        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<QuestionInvestigator>().CreateAsync(
            new AskQuestion(snapshot.RepositoryId, snapshot.Id, "Why does the first page skip results?"));
    }

    private static async Task<InvestigationRecord> GetAsync(TestDatabase database, Guid investigationId)
    {
        await using var scope = database.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IInvestigationStore>().GetAsync(investigationId))!;
    }

    private static async Task<Job?> CancelAsync(TestDatabase database, Guid jobId)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JobService>().CancelAsync(jobId);
    }

    private static async Task<Job> EnqueueIndexAsync(TestDatabase database, Guid repositoryId, string? idempotencyKey)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JobService>().EnqueueIndexAsync(repositoryId, idempotencyKey);
    }

    /// <summary>A model call that never returns until it is cancelled.</summary>
    private sealed class BlockingChatModel(TaskCompletionSource called) : IChatModel
    {
        public string ModelId => "blocking-v1";

        public async Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            called.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
