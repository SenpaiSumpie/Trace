using Trace.Domain;

namespace Trace.Application.Indexing;

public enum IndexOutcome
{
    /// <summary>A new snapshot was built and published.</summary>
    Indexed,

    /// <summary>A completed snapshot for the same commit, parser and model already exists.</summary>
    AlreadyIndexed,

    /// <summary>Another run is building the same snapshot; nothing was done.</summary>
    InProgress,
}

public sealed record IndexResult(IndexOutcome Outcome, Guid SnapshotId, string CommitSha, int ChunkCount);

/// <summary>
/// Builds an immutable index snapshot of a repository revision: chunks are staged under a
/// building snapshot, which is published in a single step once every chunk is stored.
/// </summary>
public sealed class SnapshotIndexer(ISnapshotStore store, IEmbeddingProvider embeddings, TimeProvider timeProvider)
{
    public const int EmbeddingBatchSize = 64;

    public async Task<IndexResult> IndexAsync(
        Guid repositoryId,
        IRepositorySource source,
        CancellationToken cancellationToken = default,
        Guid? jobId = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var revision = await source.ReadAsync(cancellationToken);

        var existing = await FindActiveAsync(repositoryId, revision, cancellationToken);
        if (existing is not null)
        {
            return await DescribeExistingAsync(existing, cancellationToken);
        }

        var snapshot = IndexSnapshot.Start(
            repositoryId, revision.CommitSha, revision.Branch, LineChunker.Version, embeddings.ModelId, jobId);
        if (!await store.TryAddAsync(snapshot, cancellationToken))
        {
            existing = await FindActiveAsync(repositoryId, revision, cancellationToken)
                ?? throw new InvalidOperationException(
                    "A concurrent index run for the same snapshot ended without publishing; retry.");
            return await DescribeExistingAsync(existing, cancellationToken);
        }

        try
        {
            var drafts = revision.Files.SelectMany(LineChunker.Chunk).ToList();
            foreach (var batch in drafts.Chunk(EmbeddingBatchSize))
            {
                var vectors = await embeddings.EmbedAsync(
                    [.. batch.Select(draft => draft.Text)], EmbeddingPurpose.Document, cancellationToken);
                if (vectors.Count != batch.Length)
                {
                    throw new InvalidOperationException(
                        $"Embedding provider returned {vectors.Count} vectors for {batch.Length} inputs.");
                }

                var chunks = batch
                    .Select((draft, index) => SourceChunk.Create(
                        snapshot, draft.Path, draft.Language, draft.StartLine, draft.EndLine, draft.Text, vectors[index]))
                    .ToList();
                await store.AddChunksAsync(chunks, cancellationToken);
            }

            snapshot.Complete(timeProvider.GetUtcNow());
            await store.PublishAsync(snapshot, cancellationToken);

            return new IndexResult(IndexOutcome.Indexed, snapshot.Id, snapshot.CommitSha, drafts.Count);
        }
        catch
        {
            // Not cancellable: the key must be released even when the run was cancelled.
            await store.MarkFailedAsync(snapshot.Id, CancellationToken.None);
            throw;
        }
    }

    private Task<IndexSnapshot?> FindActiveAsync(
        Guid repositoryId,
        SourceRevision revision,
        CancellationToken cancellationToken) =>
        store.FindActiveAsync(
            repositoryId, revision.CommitSha, LineChunker.Version, embeddings.ModelId, cancellationToken);

    private async Task<IndexResult> DescribeExistingAsync(IndexSnapshot existing, CancellationToken cancellationToken)
    {
        var outcome = existing.Status == SnapshotStatus.Completed
            ? IndexOutcome.AlreadyIndexed
            : IndexOutcome.InProgress;
        var chunkCount = await store.CountChunksAsync(existing.Id, cancellationToken);

        return new IndexResult(outcome, existing.Id, existing.CommitSha, chunkCount);
    }
}
