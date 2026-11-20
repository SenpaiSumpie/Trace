using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trace.Application.Jobs;
using Trace.Domain;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class JobProcessingTests(PostgresFixture postgres)
{
    private static readonly TimeSpan PastLease = TimeSpan.FromSeconds(31);

    private readonly ManualTimeProvider clock = new();
    private readonly TestJobHandler handler = new();

    [Fact]
    public async Task Job_is_run_once_and_marked_succeeded()
    {
        await using var database = await CreateAsync();
        var job = await database.EnqueueTestJobAsync(clock);

        Assert.True(await database.ProcessNextJobAsync());
        Assert.False(await database.ProcessNextJobAsync());

        var finished = await database.GetJobAsync(job.Id);
        Assert.Equal((JobStatus.Succeeded, 1), (finished.Status, finished.Attempts));
        Assert.Null(finished.LeaseToken);
        var attempt = Assert.Single(handler.Attempts);
        Assert.Equal((job.Id, 1, Job.DefaultMaxAttempts, "{}"), (attempt.JobId, attempt.Attempt, attempt.MaxAttempts, attempt.PayloadJson));
        Assert.Empty(handler.Abandoned);
    }

    [Fact]
    public async Task Enqueueing_the_same_kind_and_key_twice_keeps_one_job()
    {
        await using var database = await CreateAsync();

        var first = await database.EnqueueTestJobAsync(clock, dedupeKey: "delivery-1");
        var second = await database.EnqueueTestJobAsync(clock, dedupeKey: "delivery-1");
        var otherKind = await database.EnqueueTestJobAsync(clock, kind: "other.kind", dedupeKey: "delivery-1");

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, otherKind.Id);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.Jobs.CountAsync());
    }

    [Fact]
    public async Task Failing_job_is_retried_with_growing_backoff_until_it_succeeds()
    {
        await using var database = await CreateAsync();
        handler.Behaviour = context => context.Attempt < 3 ? throw new IOException("transient") : Task.CompletedTask;
        var job = await database.EnqueueTestJobAsync(clock);

        Assert.True(await database.ProcessNextJobAsync());
        var afterFirst = await database.GetJobAsync(job.Id);
        Assert.Equal((JobStatus.Queued, 1, JobProcessor.HandlerError), (afterFirst.Status, afterFirst.Attempts, afterFirst.ErrorCode));
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(5), afterFirst.AvailableAt);

        // Not due yet.
        Assert.False(await database.ProcessNextJobAsync());

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await database.ProcessNextJobAsync());
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(10), (await database.GetJobAsync(job.Id)).AvailableAt);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await database.ProcessNextJobAsync());

        var finished = await database.GetJobAsync(job.Id);
        Assert.Equal((JobStatus.Succeeded, 3), (finished.Status, finished.Attempts));
        Assert.Null(finished.ErrorCode);
        Assert.Empty(handler.Abandoned);
    }

    [Fact]
    public async Task Job_that_keeps_failing_is_given_up_on_after_its_last_attempt()
    {
        await using var database = await CreateAsync();
        handler.Behaviour = _ => throw new IOException("still broken; password=hunter2");
        var job = await database.EnqueueTestJobAsync(clock, maxAttempts: 2);

        Assert.True(await database.ProcessNextJobAsync());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await database.ProcessNextJobAsync());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await database.ProcessNextJobAsync());

        var failed = await database.GetJobAsync(job.Id);
        Assert.Equal((JobStatus.Failed, 2, JobProcessor.HandlerError), (failed.Status, failed.Attempts, failed.ErrorCode));
        Assert.Equal((job.Id, JobStatus.Failed, JobProcessor.HandlerError), Assert.Single(handler.Abandoned));
    }

    [Fact]
    public async Task Permanent_failure_is_not_retried()
    {
        await using var database = await CreateAsync();
        handler.Behaviour = _ => throw new JobFailedException("bad_input", "cannot ever work");
        var job = await database.EnqueueTestJobAsync(clock);

        Assert.True(await database.ProcessNextJobAsync());

        var failed = await database.GetJobAsync(job.Id);
        Assert.Equal((JobStatus.Failed, 1, "bad_input"), (failed.Status, failed.Attempts, failed.ErrorCode));
        Assert.Equal((job.Id, JobStatus.Failed, "bad_input"), Assert.Single(handler.Abandoned));
    }

    [Fact]
    public async Task Job_of_an_unknown_kind_fails_instead_of_blocking_the_queue()
    {
        await using var database = await CreateAsync();
        var unknown = await database.EnqueueTestJobAsync(clock, kind: "nobody.handles.this");
        var known = await database.EnqueueTestJobAsync(clock);

        Assert.True(await database.ProcessNextJobAsync());
        Assert.True(await database.ProcessNextJobAsync());

        Assert.Equal((JobStatus.Failed, JobProcessor.UnknownKindError), ((await database.GetJobAsync(unknown.Id)).Status, (await database.GetJobAsync(unknown.Id)).ErrorCode));
        Assert.Equal(JobStatus.Succeeded, (await database.GetJobAsync(known.Id)).Status);
    }

    [Fact]
    public async Task Job_abandoned_by_a_dead_worker_is_taken_over_after_its_lease_expires()
    {
        await using var database = await CreateAsync();
        var job = await database.EnqueueTestJobAsync(clock);

        // A worker claims the job and then dies without a word.
        var deadClaim = (await database.WithQueueAsync(queue => queue.ClaimNextAsync(TimeSpan.FromSeconds(30))))!;
        Assert.False(await database.ProcessNextJobAsync());

        clock.Advance(PastLease);
        Assert.True(await database.ProcessNextJobAsync());

        var finished = await database.GetJobAsync(job.Id);
        Assert.Equal((JobStatus.Succeeded, 2), (finished.Status, finished.Attempts));
        Assert.Equal(2, Assert.Single(handler.Attempts).Attempt);

        // If the dead worker comes back, its stale claim can no longer change anything.
        var staleToken = deadClaim.LeaseToken!.Value;
        Assert.Equal(HeartbeatResult.LeaseLost, await database.WithQueueAsync(q => q.HeartbeatAsync(job.Id, staleToken, TimeSpan.FromSeconds(30))));
        Assert.False(await database.WithQueueAsync(q => q.FailAsync(job.Id, staleToken, "late")));
        Assert.False(await database.WithQueueAsync(q => q.RetryAsync(job.Id, staleToken, TimeSpan.Zero, "late")));
        Assert.Equal(JobStatus.Succeeded, (await database.GetJobAsync(job.Id)).Status);
    }

    [Fact]
    public async Task Job_whose_worker_dies_on_every_attempt_is_failed_and_its_handler_told()
    {
        await using var database = await CreateAsync();
        var job = await database.EnqueueTestJobAsync(clock, maxAttempts: 2);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.NotNull(await database.WithQueueAsync(queue => queue.ClaimNextAsync(TimeSpan.FromSeconds(30))));
            clock.Advance(PastLease);
        }

        Assert.False(await database.ProcessNextJobAsync());

        var failed = await database.GetJobAsync(job.Id);
        Assert.Equal((JobStatus.Failed, 2, JobErrors.AttemptsExhausted), (failed.Status, failed.Attempts, failed.ErrorCode));
        Assert.Empty(handler.Attempts);
        Assert.Equal((job.Id, JobStatus.Failed, JobErrors.AttemptsExhausted), Assert.Single(handler.Abandoned));

        // Told once, not on every poll.
        Assert.False(await database.ProcessNextJobAsync());
        Assert.Single(handler.Abandoned);
    }

    [Fact]
    public async Task Worker_that_loses_its_lease_stops_and_records_nothing()
    {
        await using var database = await CreateAsync();
        var started = new TaskCompletionSource();
        handler.Behaviour = async context =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        };
        var job = await database.EnqueueTestJobAsync(clock);

        var processing = database.ProcessNextJobAsync();
        await started.Task;

        // Another worker took the job over (as after a long pause): the claim token changed.
        var newToken = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE jobs SET lease_token = {newToken}, attempts = 2 WHERE id = {job.Id}");
        }

        Assert.True(await processing);

        var current = await database.GetJobAsync(job.Id);
        Assert.Equal((JobStatus.Running, newToken, 2), (current.Status, current.LeaseToken, current.Attempts));
        Assert.Empty(handler.Abandoned);
    }

    [Fact]
    public async Task Running_job_is_cancelled_when_cancellation_is_requested()
    {
        await using var database = await CreateAsync();
        var started = new TaskCompletionSource();
        handler.Behaviour = async context =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        };
        var job = await database.EnqueueTestJobAsync(clock);

        var processing = database.ProcessNextJobAsync();
        await started.Task;
        var requested = await database.WithQueueAsync(queue => queue.RequestCancelAsync(job.Id));
        Assert.True(await processing);

        Assert.Equal(JobStatus.Running, requested!.Status);
        Assert.NotNull(requested.RequestedCancelAt);
        Assert.Equal(JobStatus.Cancelled, (await database.GetJobAsync(job.Id)).Status);
        Assert.Equal((job.Id, JobStatus.Cancelled, null), Assert.Single(handler.Abandoned));
    }

    [Fact]
    public async Task Queued_job_is_cancelled_at_once_and_never_runs()
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        var repository = await TestGraph.AddRepositoryAsync(db);
        var job = await database.EnqueueTestJobAsync(clock, repositoryId: repository.Id);

        Job? cancelled;
        await using (var scope = database.CreateScope())
        {
            cancelled = await scope.ServiceProvider.GetRequiredService<JobService>().CancelAsync(job.Id);
        }

        Assert.Equal(JobStatus.Cancelled, cancelled!.Status);
        Assert.False(await database.ProcessNextJobAsync());
        Assert.Empty(handler.Attempts);
        Assert.Equal((job.Id, JobStatus.Cancelled, null), Assert.Single(handler.Abandoned));
    }

    [Fact]
    public async Task Cancelling_a_finished_job_changes_nothing()
    {
        await using var database = await CreateAsync();
        var job = await database.EnqueueTestJobAsync(clock);
        await database.ProcessNextJobAsync();

        var result = await database.WithQueueAsync(queue => queue.RequestCancelAsync(job.Id));

        Assert.Equal(JobStatus.Succeeded, result!.Status);
        Assert.Null(result.RequestedCancelAt);
        Assert.Null(await database.WithQueueAsync(queue => queue.RequestCancelAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task Worker_shutting_down_hands_its_job_back_for_immediate_pickup()
    {
        await using var database = await CreateAsync();
        var started = new TaskCompletionSource();
        handler.Behaviour = async context =>
        {
            started.TrySetResult();
            if (context.Attempt == 1)
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }
        };
        var job = await database.EnqueueTestJobAsync(clock);

        using var shutdown = new CancellationTokenSource();
        var processing = database.ProcessNextJobAsync(shutdown.Token);
        await started.Task;
        await shutdown.CancelAsync();
        Assert.True(await processing);

        var handedBack = await database.GetJobAsync(job.Id);
        Assert.Equal((JobStatus.Queued, JobProcessor.ShutdownError), (handedBack.Status, handedBack.ErrorCode));
        Assert.Equal(clock.GetUtcNow(), handedBack.AvailableAt);
        Assert.Empty(handler.Abandoned);

        Assert.True(await database.ProcessNextJobAsync());
        Assert.Equal(JobStatus.Succeeded, (await database.GetJobAsync(job.Id)).Status);
    }

    [Fact]
    public async Task Attempt_that_runs_too_long_is_stopped_and_retried()
    {
        var options = JobTestExtensions.FastOptions();
        options.AttemptTimeout = TimeSpan.FromMilliseconds(150);
        await using var database = await CreateAsync(options);
        handler.Behaviour = context => Task.Delay(Timeout.Infinite, context.CancellationToken);
        var job = await database.EnqueueTestJobAsync(clock);

        Assert.True(await database.ProcessNextJobAsync());

        var retried = await database.GetJobAsync(job.Id);
        Assert.Equal((JobStatus.Queued, JobProcessor.TimeoutError), (retried.Status, retried.ErrorCode));
    }

    [Fact]
    public async Task Concurrent_workers_run_every_job_exactly_once()
    {
        await using var database = await CreateAsync();
        var jobs = new List<Job>();
        for (var i = 0; i < 30; i++)
        {
            jobs.Add(await database.EnqueueTestJobAsync(clock));
        }

        async Task WorkAsync()
        {
            while (await database.ProcessNextJobAsync())
            {
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(WorkAsync)));

        Assert.Equal(
            jobs.Select(job => job.Id).Order(),
            handler.Attempts.Select(attempt => attempt.JobId).Order());
        await using var db = database.CreateContext();
        Assert.Equal(30, await db.Jobs.CountAsync(job => job.Status == JobStatus.Succeeded && job.Attempts == 1));
    }

    [Fact]
    public async Task Jobs_are_claimed_in_the_order_they_become_due()
    {
        await using var database = await CreateAsync();
        var first = await database.EnqueueTestJobAsync(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await database.EnqueueTestJobAsync(clock);

        await database.ProcessNextJobAsync();
        await database.ProcessNextJobAsync();

        Assert.Equal([first.Id, second.Id], handler.Attempts.Select(attempt => attempt.JobId));
    }

    private Task<TestDatabase> CreateAsync(JobOptions? options = null) =>
        postgres.CreateDatabaseAsync(configureServices: services => services
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton(options ?? JobTestExtensions.FastOptions())
            .AddSingleton<IJobHandler>(handler));
}
