using Trace.Domain;

namespace Trace.Application.Indexing;

public interface ISnapshotStore
{
    /// <summary>The building or completed snapshot for this exact index key, if any.</summary>
    Task<IndexSnapshot?> FindActiveAsync(
        Guid repositoryId,
        string commitSha,
        string parserVersion,
        string embeddingModel,
        CancellationToken cancellationToken = default);

    /// <summary>Persists a new building snapshot. Returns false if another active snapshot holds the same key.</summary>
    Task<bool> TryAddAsync(IndexSnapshot snapshot, CancellationToken cancellationToken = default);

    Task AddChunksAsync(IReadOnlyList<SourceChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>Persists the snapshot's transition to completed, publishing it.</summary>
    Task PublishAsync(IndexSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Marks a still-building snapshot as failed so the same key can be indexed again.</summary>
    Task MarkFailedAsync(Guid snapshotId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks as failed every building snapshot of the repository that nobody is building any
    /// more: builds started by <paramref name="jobId"/> itself (an earlier attempt), by a job that
    /// is no longer running under a valid lease, or by no job and long ago.
    /// </summary>
    Task FailAbandonedBuildsAsync(Guid repositoryId, Guid jobId, CancellationToken cancellationToken = default);

    Task<int> CountChunksAsync(Guid snapshotId, CancellationToken cancellationToken = default);
}
