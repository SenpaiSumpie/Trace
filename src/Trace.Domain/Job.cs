namespace Trace.Domain;

public enum JobStatus
{
    /// <summary>Waiting to be claimed, possibly not before <see cref="Job.AvailableAt"/>.</summary>
    Queued,

    /// <summary>Claimed by a worker that holds a lease until <see cref="Job.LeaseUntil"/>.</summary>
    Running,

    Succeeded,

    /// <summary>Gave up: a non-retryable error, or every attempt was used. See <see cref="Job.ErrorCode"/>.</summary>
    Failed,

    Cancelled,
}

/// <summary>
/// A durable unit of background work. This type only describes a job and creates new ones;
/// every state change after that is a single atomic SQL statement in the job queue, because
/// claims, heartbeats and completions race between workers.
/// </summary>
public sealed class Job : Entity
{
    public const int MaxKindLength = 100;
    public const int MaxScopeLength = 200;
    public const int MaxDedupeKeyLength = 200;
    public const int MaxErrorCodeLength = 100;
    public const int DefaultMaxAttempts = 3;

    private Job()
    {
    }

    public string Kind { get; private set; } = null!;

    /// <summary>Whose work this is, as <c>repository:&lt;id&gt;</c>.</summary>
    public string TenantScope { get; private set; } = null!;

    /// <summary>Unique per <see cref="Kind"/>; makes enqueueing idempotent.</summary>
    public string DedupeKey { get; private set; } = null!;

    public string PayloadJson { get; private set; } = null!;

    public JobStatus Status { get; private set; }

    /// <summary>How many times the job has been claimed.</summary>
    public int Attempts { get; private set; }

    public int MaxAttempts { get; private set; }

    /// <summary>Earliest time the job may be claimed; pushed into the future to back off between retries.</summary>
    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    /// <summary>
    /// Identifies one claim. A worker may only heartbeat or finish the job with the token of
    /// its own claim, so a worker that lost its lease cannot overwrite a newer attempt.
    /// </summary>
    public Guid? LeaseToken { get; private set; }

    public DateTimeOffset? RequestedCancelAt { get; private set; }

    public string? ErrorCode { get; private set; }

    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

    public static string RepositoryScope(Guid repositoryId) => $"repository:{repositoryId}";

    /// <summary>The repository a job belongs to, or null if its scope is not a repository scope.</summary>
    public Guid? ScopedRepositoryId =>
        TenantScope.StartsWith("repository:", StringComparison.Ordinal)
        && Guid.TryParse(TenantScope.AsSpan("repository:".Length), out var repositoryId)
            ? repositoryId
            : null;

    public static Job Enqueue(
        string kind,
        string tenantScope,
        string dedupeKey,
        string payloadJson,
        DateTimeOffset availableAt,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        return new Job
        {
            Kind = Guard.Required(kind, MaxKindLength, nameof(kind)),
            TenantScope = Guard.Required(tenantScope, MaxScopeLength, nameof(tenantScope)),
            DedupeKey = Guard.Required(dedupeKey, MaxDedupeKeyLength, nameof(dedupeKey)),
            PayloadJson = payloadJson,
            Status = JobStatus.Queued,
            MaxAttempts = maxAttempts,
            AvailableAt = availableAt.ToUniversalTime(),
        };
    }
}
