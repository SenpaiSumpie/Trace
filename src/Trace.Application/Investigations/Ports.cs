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

public sealed record InvestigationRecord(
    Investigation Investigation,
    string CommitSha,
    IReadOnlyList<InvestigationEvent> Events);

public interface IInvestigationStore
{
    Task<Investigation?> FindByIdempotencyKeyAsync(
        Guid repositoryId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Persists a new investigation. Returns false if its idempotency key is already taken.</summary>
    Task<bool> TryAddAsync(Investigation investigation, CancellationToken cancellationToken = default);

    Task AppendEventAsync(InvestigationEvent investigationEvent, CancellationToken cancellationToken = default);

    /// <summary>Persists the investigation's current state together with its evidence, atomically.</summary>
    Task SaveAsync(
        Investigation investigation,
        IReadOnlyList<EvidenceRef> evidence,
        CancellationToken cancellationToken = default);

    /// <summary>An investigation with its events, only if its repository is visible.</summary>
    Task<InvestigationRecord?> GetAsync(Guid investigationId, CancellationToken cancellationToken = default);
}
