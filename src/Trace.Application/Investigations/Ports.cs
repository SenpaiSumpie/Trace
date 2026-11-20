using Trace.Domain;

namespace Trace.Application.Investigations;

public sealed record SnapshotSummary(Guid Id, string CommitSha, string Branch, DateTimeOffset CompletedAt);

public sealed record RepositorySummary(
    Guid Id,
    string Owner,
    string Name,
    string DefaultBranch,
    SnapshotSummary? LatestSnapshot);

/// <summary>
/// Repositories visible to API callers. Until authentication exists there are no users to
/// authorize, so only repositories marked public are ever visible.
/// </summary>
public interface IRepositoryCatalog
{
    Task<IReadOnlyList<RepositorySummary>> ListVisibleAsync(CancellationToken cancellationToken = default);

    Task<bool> IsVisibleAsync(Guid repositoryId, CancellationToken cancellationToken = default);
}

/// <param name="JobId">The background job running the investigation, if it was queued through one.</param>
public sealed record InvestigationRecord(
    Investigation Investigation,
    string CommitSha,
    IReadOnlyList<InvestigationEvent> Events,
    Guid? JobId);

public interface IInvestigationStore
{
    Task<Investigation?> FindByIdempotencyKeyAsync(
        Guid repositoryId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a new investigation and the job that will run it, atomically. Returns false,
    /// storing neither, if the investigation's idempotency key is already taken.
    /// </summary>
    Task<bool> TryAddAsync(Investigation investigation, Job job, CancellationToken cancellationToken = default);

    /// <summary>The investigation, tracked for changes, regardless of repository visibility (workers only).</summary>
    Task<Investigation?> FindForUpdateAsync(Guid investigationId, CancellationToken cancellationToken = default);

    /// <summary>The highest event sequence number recorded so far, or 0.</summary>
    Task<int> GetLastEventSeqAsync(Guid investigationId, CancellationToken cancellationToken = default);

    Task AppendEventAsync(InvestigationEvent investigationEvent, CancellationToken cancellationToken = default);

    /// <summary>Persists the investigation's current state together with its evidence, atomically.</summary>
    Task SaveAsync(
        Investigation investigation,
        IReadOnlyList<EvidenceRef> evidence,
        CancellationToken cancellationToken = default);

    /// <summary>Model tokens (input + output) spent by all investigations created since the given time.</summary>
    Task<long> SumTokensSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default);

    /// <summary>An investigation with its events, only if its repository is visible.</summary>
    Task<InvestigationRecord?> GetAsync(Guid investigationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Spending limits for model calls. Both fail closed: when a limit is reached no further model
/// call is made. There are no users yet, so the daily budget is shared by every caller.
/// </summary>
public sealed class ChatLimits
{
    /// <summary>Input plus output tokens one investigation may use, across all its attempts.</summary>
    public long MaxTokensPerInvestigation { get; set; } = 300_000;

    /// <summary>Input plus output tokens all investigations together may use in any 24 hours.</summary>
    public long DailyTokenBudget { get; set; } = 3_000_000;
}
