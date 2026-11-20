using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trace.Application.Jobs;
using Trace.Domain;
using Trace.Infrastructure.Persistence;

namespace Trace.Infrastructure.Jobs;

/// <summary>
/// The job queue on PostgreSQL. Every transition is one UPDATE whose WHERE clause states the
/// precondition (current status, lease token), so concurrent workers cannot both win, and a
/// worker that lost its lease changes nothing. Claims use FOR UPDATE SKIP LOCKED so workers
/// never wait on each other. All times come from <see cref="TimeProvider"/>, not the database
/// clock, so every instance must have a synchronized clock.
/// </summary>
internal sealed class EfJobQueue(TraceDbContext db, TimeProvider timeProvider) : IJobQueue
{
    public async Task<Job> EnqueueAsync(Job job, CancellationToken cancellationToken = default)
    {
        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(job).State = EntityState.Detached;
            return job;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: TraceDbContext.JobDedupeIndex,
        })
        {
            db.Entry(job).State = EntityState.Detached;
            return await FindAsync(job.Kind, job.DedupeKey, cancellationToken)
                ?? throw new InvalidOperationException("Job dedupe conflict without an existing job.");
        }
    }

    public Task<Job?> FindAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        db.Jobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public Task<Job?> FindAsync(string kind, string dedupeKey, CancellationToken cancellationToken = default) =>
        db.Jobs.AsNoTracking().SingleOrDefaultAsync(
            job => job.Kind == kind && job.DedupeKey == dedupeKey, cancellationToken);

    public async Task<IReadOnlyList<Job>> FailExhaustedAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        return await db.Jobs
            .FromSql($"""
                UPDATE jobs
                SET status = 'failed', error_code = {JobErrors.AttemptsExhausted},
                    lease_until = NULL, lease_token = NULL, updated_at = {now}
                WHERE status = 'running' AND lease_until < {now} AND attempts >= max_attempts
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<Job?> ClaimNextAsync(TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var leaseUntil = now + leaseDuration;
        var leaseToken = Guid.NewGuid();

        // Due queued jobs, plus running jobs whose worker stopped renewing its lease.
        var claimed = await db.Jobs
            .FromSql($"""
                UPDATE jobs
                SET status = 'running', attempts = attempts + 1,
                    lease_until = {leaseUntil}, lease_token = {leaseToken}, updated_at = {now}
                WHERE id = (
                    SELECT id FROM jobs
                    WHERE (status = 'queued' AND available_at <= {now})
                       OR (status = 'running' AND lease_until < {now} AND attempts < max_attempts)
                    ORDER BY available_at, created_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed.SingleOrDefault();
    }

    public async Task<HeartbeatResult> HeartbeatAsync(
        Guid jobId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var leaseUntil = now + leaseDuration;
        var renewed = await db.Jobs
            .FromSql($"""
                UPDATE jobs SET lease_until = {leaseUntil}, updated_at = {now}
                WHERE id = {jobId} AND lease_token = {leaseToken} AND status = 'running'
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return renewed.SingleOrDefault() switch
        {
            null => HeartbeatResult.LeaseLost,
            { RequestedCancelAt: not null } => HeartbeatResult.CancelRequested,
            _ => HeartbeatResult.Alive,
        };
    }

    public Task<bool> SucceedAsync(Guid jobId, Guid leaseToken, CancellationToken cancellationToken = default) =>
        FinishAsync(jobId, leaseToken, "succeeded", errorCode: null, cancellationToken);

    public Task<bool> FailAsync(
        Guid jobId,
        Guid leaseToken,
        string errorCode,
        CancellationToken cancellationToken = default) =>
        FinishAsync(jobId, leaseToken, "failed", errorCode, cancellationToken);

    public Task<bool> CancelAsync(Guid jobId, Guid leaseToken, CancellationToken cancellationToken = default) =>
        FinishAsync(jobId, leaseToken, "cancelled", errorCode: null, cancellationToken);

    public async Task<bool> RetryAsync(
        Guid jobId,
        Guid leaseToken,
        TimeSpan delay,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var availableAt = now + delay;
        return await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE jobs
            SET status = 'queued', available_at = {availableAt}, error_code = {errorCode},
                lease_until = NULL, lease_token = NULL, updated_at = {now}
            WHERE id = {jobId} AND lease_token = {leaseToken} AND status = 'running'
            """,
            cancellationToken) == 1;
    }

    public async Task<Job?> RequestCancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var updated = await db.Jobs
            .FromSql($"""
                UPDATE jobs
                SET requested_cancel_at = COALESCE(requested_cancel_at, {now}),
                    status = CASE WHEN status = 'queued' THEN 'cancelled' ELSE status END,
                    updated_at = {now}
                WHERE id = {jobId} AND status IN ('queued', 'running')
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return updated.SingleOrDefault() ?? await FindAsync(jobId, cancellationToken);
    }

    private async Task<bool> FinishAsync(
        Guid jobId,
        Guid leaseToken,
        string status,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE jobs
            SET status = {status}, error_code = {errorCode},
                lease_until = NULL, lease_token = NULL, updated_at = {now}
            WHERE id = {jobId} AND lease_token = {leaseToken} AND status = 'running'
            """,
            cancellationToken) == 1;
    }
}

internal sealed class JobSignal : IJobSignal, IDisposable
{
    private readonly SemaphoreSlim pending = new(0, 1);

    public void Notify()
    {
        try
        {
            pending.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        pending.WaitAsync(timeout, cancellationToken);

    public void Dispose() => pending.Dispose();
}
