using Microsoft.Extensions.Logging;
using Trace.Domain;

namespace Trace.Application.Jobs;

/// <summary>
/// Runs one job at a time: claims it, keeps its lease alive while the handler works, and
/// records the outcome. Scoped: create one per job so each attempt gets fresh services.
/// </summary>
public sealed partial class JobProcessor(
    IJobQueue queue,
    IJobLeaseKeeper leaseKeeper,
    IEnumerable<IJobHandler> handlers,
    JobOptions options,
    TimeProvider timeProvider,
    ILogger<JobProcessor> logger)
{
    public const string UnknownKindError = "unknown_job_kind";
    public const string HandlerError = "handler_error";
    public const string TimeoutError = "attempt_timed_out";
    public const string ShutdownError = "worker_shutdown";

    /// <summary>
    /// Processes at most one job. Returns false if there was nothing to do.
    /// <paramref name="shutdown"/> signals that this worker is stopping: the job in progress is
    /// handed back to the queue rather than failed.
    /// </summary>
    public async Task<bool> ProcessNextAsync(CancellationToken shutdown = default)
    {
        foreach (var dead in await queue.FailExhaustedAsync(shutdown))
        {
            await NotifyAbandonedAsync(dead, JobStatus.Failed, dead.ErrorCode);
        }

        var job = await queue.ClaimNextAsync(options.LeaseDuration, shutdown);
        if (job is null)
        {
            return false;
        }

        var leaseToken = job.LeaseToken!.Value;
        var handler = handlers.FirstOrDefault(candidate => candidate.Kind == job.Kind);
        if (handler is null)
        {
            await queue.FailAsync(job.Id, leaseToken, UnknownKindError, CancellationToken.None);
            return true;
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        using var timeout = new CancellationTokenSource(options.AttemptTimeout, timeProvider);
        using var timeoutRegistration = timeout.Token.Register(attempt.Cancel);
        var lease = new LeaseState();
        var heartbeat = KeepLeaseAsync(job.Id, leaseToken, lease, attempt);

        Exception? failure = null;
        try
        {
            await handler.HandleAsync(new JobContext(job.Id, job.PayloadJson, job.Attempts, job.MaxAttempts, attempt.Token));
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await attempt.CancelAsync();
        await heartbeat;

        if (lease.Lost)
        {
            // Another worker owns the job now; whatever happened here is not ours to record.
            return true;
        }

        if (failure is null)
        {
            await queue.SucceedAsync(job.Id, leaseToken, CancellationToken.None);
        }
        else if (lease.CancelRequested)
        {
            if (await queue.CancelAsync(job.Id, leaseToken, CancellationToken.None))
            {
                await NotifyAbandonedAsync(job, JobStatus.Cancelled, errorCode: null);
            }
        }
        else if (failure is OperationCanceledException && shutdown.IsCancellationRequested)
        {
            // Not the job's fault: make it available immediately for another worker.
            await queue.RetryAsync(job.Id, leaseToken, TimeSpan.Zero, ShutdownError, CancellationToken.None);
        }
        else
        {
            await RecordFailureAsync(job, leaseToken, failure, timedOut: timeout.IsCancellationRequested);
        }

        return true;
    }

    private async Task RecordFailureAsync(Job job, Guid leaseToken, Exception failure, bool timedOut)
    {
        var permanent = failure as JobFailedException;
        var errorCode = permanent?.ErrorCode ?? (timedOut ? TimeoutError : HandlerError);
        var willRetry = permanent is null && job.Attempts < job.MaxAttempts;

        // The database keeps only the error code; the exception itself goes to the log.
        LogAttemptFailed(logger, failure, job.Id, job.Kind, job.Attempts, job.MaxAttempts, errorCode, willRetry);

        if (willRetry)
        {
            await queue.RetryAsync(
                job.Id, leaseToken, options.RetryDelay(job.Attempts), errorCode, CancellationToken.None);
            return;
        }

        if (await queue.FailAsync(job.Id, leaseToken, errorCode, CancellationToken.None))
        {
            await NotifyAbandonedAsync(job, JobStatus.Failed, errorCode);
        }
    }

    private async Task KeepLeaseAsync(Guid jobId, Guid leaseToken, LeaseState lease, CancellationTokenSource attempt)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.HeartbeatInterval, timeProvider, attempt.Token);

                // Not through queue: that shares the handler's database context, which is busy right now.
                var result = await leaseKeeper.RenewAsync(jobId, leaseToken, options.LeaseDuration, CancellationToken.None);
                if (result == HeartbeatResult.Alive)
                {
                    continue;
                }

                lease.Lost = result == HeartbeatResult.LeaseLost;
                lease.CancelRequested = result == HeartbeatResult.CancelRequested;
                await attempt.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The attempt ended; stop heartbeating.
        }
    }

    private async Task NotifyAbandonedAsync(Job job, JobStatus finalStatus, string? errorCode)
    {
        if (handlers.FirstOrDefault(candidate => candidate.Kind == job.Kind) is { } handler)
        {
            await handler.OnAbandonedAsync(job.Id, job.PayloadJson, finalStatus, errorCode, CancellationToken.None);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job {JobId} ({Kind}) attempt {Attempt} of {MaxAttempts} failed with {ErrorCode}; retry: {WillRetry}.")]
    private static partial void LogAttemptFailed(
        ILogger logger,
        Exception exception,
        Guid jobId,
        string kind,
        int attempt,
        int maxAttempts,
        string errorCode,
        bool willRetry);

    private sealed class LeaseState
    {
        public volatile bool Lost;
        public volatile bool CancelRequested;
    }
}
