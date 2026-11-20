using Microsoft.EntityFrameworkCore;
using Pgvector;
using Trace.Application.Retrieval;
using Trace.Domain;

namespace Trace.Infrastructure.Persistence;

internal sealed class EfSnapshotReader(TraceDbContext db) : ISnapshotReader
{
    public Task<IndexSnapshot?> FindSnapshotAsync(
        Guid repositoryId,
        Guid snapshotId,
        CancellationToken cancellationToken = default) =>
        db.IndexSnapshots.AsNoTracking().SingleOrDefaultAsync(
            snapshot => snapshot.Id == snapshotId && snapshot.RepositoryId == repositoryId, cancellationToken);

    public async Task<IReadOnlyList<ChunkRecord>> SearchLexicalAsync(
        Guid snapshotId,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // plainto_tsquery ANDs the words; a natural-language question rarely has every word in
        // one chunk, so the query is relaxed to OR and ranking decides. The query text is only
        // ever a bound parameter.
        var chunks = await db.SourceChunks
            .FromSql($"""
                SELECT c.*
                FROM source_chunks c
                CROSS JOIN (
                    SELECT replace(
                        plainto_tsquery('english', trace_fts_normalize({query}))::text, '&', '|')::tsquery AS terms
                ) q
                WHERE c.snapshot_id = {snapshotId} AND c.search_vector @@ q.terms
                ORDER BY ts_rank_cd(c.search_vector, q.terms) DESC, c.path, c.start_line
                LIMIT {limit}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. chunks.Select(ToRecord)];
    }

    public async Task<IReadOnlyList<ChunkRecord>> SearchVectorAsync(
        Guid snapshotId,
        float[] embedding,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Exact (non-approximate) search within the snapshot: see ADR-003.
        var vector = new Vector(embedding);
        var chunks = await db.SourceChunks
            .FromSql($"""
                SELECT c.*
                FROM source_chunks c
                WHERE c.snapshot_id = {snapshotId}
                ORDER BY c.embedding <=> {vector}, c.path, c.start_line
                LIMIT {limit}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. chunks.Select(ToRecord)];
    }

    public Task<int?> FindLastLineAsync(Guid snapshotId, string path, CancellationToken cancellationToken = default) =>
        db.SourceChunks
            .Where(chunk => chunk.SnapshotId == snapshotId && chunk.Path == path)
            .MaxAsync(chunk => (int?)chunk.EndLine, cancellationToken);

    public async Task<IReadOnlyList<ChunkRecord>> GetChunksInRangeAsync(
        Guid snapshotId,
        string path,
        int startLine,
        int endLine,
        CancellationToken cancellationToken = default) =>
        await db.SourceChunks
            .Where(chunk => chunk.SnapshotId == snapshotId
                && chunk.Path == path
                && chunk.StartLine <= endLine
                && chunk.EndLine >= startLine)
            .OrderBy(chunk => chunk.StartLine)
            .Select(chunk => new ChunkRecord(
                chunk.Id, chunk.Path, chunk.Language, chunk.StartLine, chunk.EndLine, chunk.Text))
            .ToListAsync(cancellationToken);

    private static ChunkRecord ToRecord(SourceChunk chunk) =>
        new(chunk.Id, chunk.Path, chunk.Language, chunk.StartLine, chunk.EndLine, chunk.Text);
}
