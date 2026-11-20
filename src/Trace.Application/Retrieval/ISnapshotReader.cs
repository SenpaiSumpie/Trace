using Trace.Domain;

namespace Trace.Application.Retrieval;

public sealed record ChunkRecord(Guid Id, string Path, string Language, int StartLine, int EndLine, string Text);

/// <summary>Read-only access to indexed content. Every chunk query is confined to one snapshot.</summary>
public interface ISnapshotReader
{
    /// <summary>The snapshot, only if it belongs to the given repository.</summary>
    Task<IndexSnapshot?> FindSnapshotAsync(Guid repositoryId, Guid snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Chunks matching any query word, best match first.</summary>
    Task<IReadOnlyList<ChunkRecord>> SearchLexicalAsync(
        Guid snapshotId, string query, int limit, CancellationToken cancellationToken = default);

    /// <summary>Chunks nearest to the embedding by cosine distance, nearest first.</summary>
    Task<IReadOnlyList<ChunkRecord>> SearchVectorAsync(
        Guid snapshotId, float[] embedding, int limit, CancellationToken cancellationToken = default);

    /// <summary>The last indexed line of a file, or null if the path is not in the snapshot.</summary>
    Task<int?> FindLastLineAsync(Guid snapshotId, string path, CancellationToken cancellationToken = default);

    /// <summary>Chunks of one file that overlap the inclusive line range, in line order.</summary>
    Task<IReadOnlyList<ChunkRecord>> GetChunksInRangeAsync(
        Guid snapshotId, string path, int startLine, int endLine, CancellationToken cancellationToken = default);
}
