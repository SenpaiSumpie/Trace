using Trace.Application.Indexing;
using Trace.Application.Investigations;
using Trace.Domain;

namespace Trace.Application.Jobs;

public static class JobErrors
{
    public const string AttemptsExhausted = "attempts_exhausted";
    public const string InvalidPayload = "invalid_payload";
}

public enum JobRequestError
{
    RepositoryNotFound,
    NoSource,
}

public sealed class JobRequestException(JobRequestError error, string message) : Exception(message)
{
    public JobRequestError Error { get; } = error;
}

/// <summary>
/// What API callers may do with jobs. A job is only visible through the repository it belongs
/// to, so the same visibility rule applies as everywhere else.
/// </summary>
public sealed class JobService(
    IJobQueue queue,
    IJobSignal signal,
    IRepositoryCatalog repositories,
    IRepositorySourceResolver sources,
    IEnumerable<IJobHandler> handlers,
    TimeProvider timeProvider)
{
    public async Task<Job> EnqueueIndexAsync(
        Guid repositoryId,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!await repositories.IsVisibleAsync(repositoryId, cancellationToken))
        {
            throw new JobRequestException(JobRequestError.RepositoryNotFound, "The repository does not exist.");
        }

        if (await sources.ResolveAsync(repositoryId, cancellationToken) is null)
        {
            throw new JobRequestException(JobRequestError.NoSource, "The repository has no source to index.");
        }

        var job = await queue.EnqueueAsync(
            Job.Enqueue(
                IndexJobHandler.JobKind,
                Job.RepositoryScope(repositoryId),
                // Scoped to the repository so one caller's key cannot collide with another repository's.
                dedupeKey: $"{repositoryId}:{idempotencyKey ?? Guid.NewGuid().ToString()}",
                IndexJobHandler.Payload(repositoryId),
                timeProvider.GetUtcNow()),
            cancellationToken);
        signal.Notify();

        return job;
    }

    public async Task<Job?> FindAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await queue.FindAsync(jobId, cancellationToken);
        return job is not null && await IsVisibleAsync(job, cancellationToken) ? job : null;
    }

    /// <summary>Requests cancellation. Returns the job, or null if it does not exist or is not visible.</summary>
    public async Task<Job?> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(jobId, cancellationToken) is null)
        {
            return null;
        }

        var job = await queue.RequestCancelAsync(jobId, cancellationToken);
        if (job is { Status: JobStatus.Cancelled })
        {
            // A queued job is cancelled without any worker seeing it, so tell its handler here.
            // Handlers ignore this if the work already has a final state.
            if (handlers.FirstOrDefault(candidate => candidate.Kind == job.Kind) is { } handler)
            {
                await handler.OnAbandonedAsync(job.Id, job.PayloadJson, JobStatus.Cancelled, errorCode: null, cancellationToken);
            }
        }

        return job;
    }

    private async Task<bool> IsVisibleAsync(Job job, CancellationToken cancellationToken) =>
        job.ScopedRepositoryId is { } repositoryId && await repositories.IsVisibleAsync(repositoryId, cancellationToken);
}
