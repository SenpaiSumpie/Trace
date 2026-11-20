namespace Trace.Domain;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// A durable unit of background work. Only the record and its dedupe rule exist so far;
/// claiming, leases, retries and cancellation handling arrive with the job infrastructure.
/// </summary>
public sealed class Job : Entity
{
    public const int MaxKindLength = 100;
    public const int MaxScopeLength = 200;
    public const int MaxDedupeKeyLength = 200;

    private Job()
    {
    }

    public string Kind { get; private set; } = null!;

    /// <summary>Opaque key identifying whose work this is, e.g. <c>repository:&lt;id&gt;</c>.</summary>
    public string TenantScope { get; private set; } = null!;

    /// <summary>Unique per <see cref="Kind"/>; makes enqueueing idempotent.</summary>
    public string DedupeKey { get; private set; } = null!;

    public string PayloadJson { get; private set; } = null!;

    public JobStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public DateTimeOffset? RequestedCancelAt { get; private set; }

    public string? ErrorCode { get; private set; }

    public static Job Enqueue(string kind, string tenantScope, string dedupeKey, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        return new Job
        {
            Kind = Guard.Required(kind, MaxKindLength, nameof(kind)),
            TenantScope = Guard.Required(tenantScope, MaxScopeLength, nameof(tenantScope)),
            DedupeKey = Guard.Required(dedupeKey, MaxDedupeKeyLength, nameof(dedupeKey)),
            PayloadJson = payloadJson,
            Status = JobStatus.Queued,
        };
    }
}
