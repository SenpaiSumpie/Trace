using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Trace.Application.Jobs;
using Trace.Domain;

namespace Trace.IntegrationTests.Support;

/// <summary>A clock tests move by hand. Timers still run in real time.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long ticks = start.UtcTicks;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
}

/// <summary>A job handler whose behaviour a test supplies, and which records what happened to it.</summary>
public sealed class TestJobHandler(string kind = TestJobHandler.DefaultKind) : IJobHandler
{
    public const string DefaultKind = "test.job";

    public string Kind => kind;

    public Func<JobContext, Task> Behaviour { get; set; } = _ => Task.CompletedTask;

    public ConcurrentQueue<JobContext> Attempts { get; } = new();

    public ConcurrentQueue<(Guid JobId, JobStatus Status, string? ErrorCode)> Abandoned { get; } = new();

    public async Task HandleAsync(JobContext context)
    {
        Attempts.Enqueue(context);
        await Behaviour(context);
    }

    public Task OnAbandonedAsync(
        Guid jobId,
        string payloadJson,
        JobStatus finalStatus,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        Abandoned.Enqueue((jobId, finalStatus, errorCode));
        return Task.CompletedTask;
    }
}

internal static class JobTestExtensions
{
    /// <summary>Short real-time heartbeats; leases and backoff are driven by the manual clock.</summary>
    public static JobOptions FastOptions() => new()
    {
        HeartbeatInterval = TimeSpan.FromMilliseconds(40),
        LeaseDuration = TimeSpan.FromSeconds(30),
        RetryBaseDelay = TimeSpan.FromSeconds(5),
        RetryMaxDelay = TimeSpan.FromSeconds(60),
        AttemptTimeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>Processes one job in its own scope, as one iteration of a worker loop does.</summary>
    public static async Task<bool> ProcessNextJobAsync(this TestDatabase database, CancellationToken shutdown = default)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessNextAsync(shutdown);
    }

    public static async Task<TResult> WithQueueAsync<TResult>(this TestDatabase database, Func<IJobQueue, Task<TResult>> use)
    {
        await using var scope = database.CreateScope();
        return await use(scope.ServiceProvider.GetRequiredService<IJobQueue>());
    }

    public static Task<Job> EnqueueTestJobAsync(
        this TestDatabase database,
        TimeProvider clock,
        string kind = TestJobHandler.DefaultKind,
        string? dedupeKey = null,
        int maxAttempts = Job.DefaultMaxAttempts,
        Guid? repositoryId = null) =>
        database.WithQueueAsync(queue => queue.EnqueueAsync(Job.Enqueue(
            kind,
            Job.RepositoryScope(repositoryId ?? Guid.NewGuid()),
            dedupeKey ?? Guid.NewGuid().ToString(),
            "{}",
            clock.GetUtcNow(),
            maxAttempts)));

    public static async Task<Job> GetJobAsync(this TestDatabase database, Guid jobId) =>
        (await database.WithQueueAsync(queue => queue.FindAsync(jobId)))!;
}
