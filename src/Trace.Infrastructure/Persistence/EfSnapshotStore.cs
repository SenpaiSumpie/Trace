using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trace.Application.Indexing;
using Trace.Domain;

namespace Trace.Infrastructure.Persistence;

internal sealed class EfSnapshotStore(TraceDbContext db, TimeProvider timeProvider) : ISnapshotStore
{
    public Task<IndexSnapshot?> FindActiveAsync(
        Guid repositoryId,
        string commitSha,
        string parserVersion,
        string embeddingModel,
        CancellationToken cancellationToken = default) =>
        db.IndexSnapshots.AsNoTracking().SingleOrDefaultAsync(
            s => s.RepositoryId == repositoryId
                && s.CommitSha == commitSha
                && s.ParserVersion == parserVersion
                && s.EmbeddingModel == embeddingModel
                && s.Status != SnapshotStatus.Failed,
            cancellationToken);

    public async Task<bool> TryAddAsync(IndexSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        db.IndexSnapshots.Add(snapshot);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: TraceDbContext.ActiveSnapshotKeyIndex,
        })
        {
            db.Entry(snapshot).State = EntityState.Detached;
            return false;
        }
    }

    public async Task AddChunksAsync(IReadOnlyList<SourceChunk> chunks, CancellationToken cancellationToken = default)
    {
        db.SourceChunks.AddRange(chunks);
        await db.SaveChangesAsync(cancellationToken);

        // Chunks are write-once; do not keep every chunk of a repository tracked in memory.
        foreach (var chunk in chunks)
        {
            db.Entry(chunk).State = EntityState.Detached;
        }
    }

    public Task PublishAsync(IndexSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.Status != SnapshotStatus.Completed)
        {
            throw new InvalidOperationException("Only a completed snapshot can be published.");
        }

        db.IndexSnapshots.Update(snapshot);
        return db.SaveChangesAsync(cancellationToken);
    }

    public Task MarkFailedAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        // The context may hold changes from the failed operation; never flush them.
        db.ChangeTracker.Clear();
        var now = timeProvider.GetUtcNow();
        return db.IndexSnapshots
            .Where(s => s.Id == snapshotId && s.Status == SnapshotStatus.Building)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(s => s.Status, SnapshotStatus.Failed)
                    .SetProperty(s => s.UpdatedAt, now),
                cancellationToken);
    }

    public Task<int> CountChunksAsync(Guid snapshotId, CancellationToken cancellationToken = default) =>
        db.SourceChunks.CountAsync(c => c.SnapshotId == snapshotId, cancellationToken);
}
