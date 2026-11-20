using Trace.Domain;

namespace Trace.Application.Jobs;

public sealed class JobOptions
{
    /// <summary>How long a claim lasts without a heartbeat before another worker may take the job.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long an idle worker waits before looking for work again.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A single attempt is cancelled and retried after this long.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Capped exponential backoff: base, 2x base, 4x base, ... up to the maximum.</summary>
    public TimeSpan RetryDelay(int attemptsSoFar)
    {
        var factor = Math.Pow(2, Math.Clamp(attemptsSoFar - 1, 0, 30));
        return TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
    }
}

/// <summary>What a worker learns when it extends its lease.</summary>
public enum HeartbeatResult
{
    /// <summary>The lease was extended.</summary>
    Alive,

    /// <summary>The lease was extended and someone asked for the job to be cancelled.</summary>
    CancelRequested,

    /// <summary>The job is no longer this worker's: its work must stop and must not be recorded.</summary>
    LeaseLost,
}

/// <summary>
/// The durable job queue. Each method is one atomic statement, so any number of workers and
/// API instances can use it concurrently. Delivery is at-least-once: handlers must be idempotent.
/// </summary>
public interface IJobQueue
{
    /// <summary>Adds the job, or returns the existing job with the same kind and dedupe key.</summary>
    Task<Job> EnqueueAsync(Job job, CancellationToken cancellationToken = default);

    Task<Job?> FindAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<Job?> FindAsync(string kind, string dedupeKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fails jobs whose worker died on their last allowed attempt, and returns them so their
    /// handlers can clean up.
    /// </summary>
    Task<IReadOnlyList<Job>> FailExhaustedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the next job that is due, or whose previous worker's lease expired, and marks it
    /// running under a new lease. Returns null when there is nothing to do.
    /// </summary>
    Task<Job?> ClaimNextAsync(TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    Task<HeartbeatResult> HeartbeatAsync(
        Guid jobId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>Each of these returns false if the lease was lost and nothing was recorded.</summary>
    Task<bool> SucceedAsync(Guid jobId, Guid leaseToken, CancellationToken cancellationToken = default);

    Task<bool> FailAsync(Guid jobId, Guid leaseToken, string errorCode, CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(Guid jobId, Guid leaseToken, CancellationToken cancellationToken = default);

    /// <summary>Puts the job back in the queue, claimable after <paramref name="delay"/>.</summary>
    Task<bool> RetryAsync(
        Guid jobId, Guid leaseToken, TimeSpan delay, string errorCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for cancellation: a queued job is cancelled at once, a running job when its worker
    /// next heartbeats. Does nothing to a finished job. Returns the job, or null if unknown.
    /// </summary>
    Task<Job?> RequestCancelAsync(Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Renews a running job's lease. It runs concurrently with the job's handler, so an
/// implementation must not share a database connection or context with it.
/// </summary>
public interface IJobLeaseKeeper
{
    Task<HeartbeatResult> RenewAsync(
        Guid jobId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
}

/// <summary>Wakes idle workers in this process when a job is enqueued, instead of waiting for the next poll.</summary>
public interface IJobSignal
{
    void Notify();

    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record JobContext(Guid JobId, string PayloadJson, int Attempt, int MaxAttempts, CancellationToken CancellationToken)
{
    public bool IsLastAttempt => Attempt >= MaxAttempts;
}

/// <summary>
/// Thrown by a handler for an error that will not go away on retry. Any other exception is
/// treated as transient and retried while attempts remain.
/// </summary>
public sealed class JobFailedException(string errorCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;
}

public interface IJobHandler
{
    string Kind { get; }

    /// <summary>
    /// Does the work. Must be safe to run again after a partial earlier attempt. Returning
    /// normally means the job succeeded.
    /// </summary>
    Task HandleAsync(JobContext context);

    /// <summary>
    /// Called once when the job has failed for good or was cancelled, so the handler can leave
    /// the thing the job was for in a matching final state.
    /// </summary>
    Task OnAbandonedAsync(
        Guid jobId,
        string payloadJson,
        JobStatus finalStatus,
        string? errorCode,
        CancellationToken cancellationToken);
}
