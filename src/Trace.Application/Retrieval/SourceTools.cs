using Trace.Application.Indexing;

namespace Trace.Application.Retrieval;

public sealed record CodeSearchHit(
    Guid ChunkId,
    string Path,
    string Language,
    int StartLine,
    int EndLine,
    string Excerpt,
    double Score,
    int? LexicalRank,
    int? VectorRank);

public sealed record CodeSearchResult(string CommitSha, IReadOnlyList<CodeSearchHit> Hits);

public sealed record SourceLine(int Number, string Text);

public sealed record FileRange(string CommitSha, string Path, int StartLine, int EndLine, IReadOnlyList<SourceLine> Lines);

/// <summary>
/// The read-only tools an investigation may use: <c>search_code</c> and <c>read_file_range</c>.
/// Inputs and outputs are bounded, and every call is confined to one completed snapshot of one
/// repository. Callers must already have established that the user may read that repository.
/// </summary>
public sealed class SourceTools(ISnapshotReader reader, IEmbeddingProvider embeddings)
{
    public const int MaxQueryLength = 1_000;
    public const int MaxSearchLimit = 20;
    public const int MaxRangeLines = 200;

    /// <summary>How many candidates each retriever contributes before fusion.</summary>
    private const int CandidatesPerRetriever = 50;

    /// <summary><c>search_code</c>: hybrid lexical + semantic search, fused by reciprocal rank.</summary>
    public async Task<CodeSearchResult> SearchCodeAsync(
        Guid repositoryId,
        Guid snapshotId,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // PostgreSQL text cannot contain NUL; treat it as the separator it most resembles.
        query = query?.Replace('\0', ' ')!;
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaxQueryLength)
        {
            throw new SourceToolException(
                SourceToolError.InvalidArgument, $"query must be 1 to {MaxQueryLength} characters.");
        }

        if (limit is < 1 or > MaxSearchLimit)
        {
            throw new SourceToolException(
                SourceToolError.InvalidArgument, $"limit must be between 1 and {MaxSearchLimit}.");
        }

        var snapshot = await SnapshotAccess.RequireCompletedAsync(reader, repositoryId, snapshotId, cancellationToken);
        if (snapshot.EmbeddingModel != embeddings.ModelId)
        {
            throw new SourceToolException(
                SourceToolError.EmbeddingModelMismatch,
                "The snapshot was indexed with a different embedding model and must be re-indexed before it can be searched.");
        }

        var queryEmbedding = (await embeddings.EmbedAsync([query], cancellationToken))[0];
        var lexical = await reader.SearchLexicalAsync(snapshot.Id, query, CandidatesPerRetriever, cancellationToken);
        var vector = await reader.SearchVectorAsync(snapshot.Id, queryEmbedding, CandidatesPerRetriever, cancellationToken);

        var hits = ReciprocalRankFusion.Fuse(lexical, vector, limit)
            .Select(fused => new CodeSearchHit(
                fused.Chunk.Id,
                fused.Chunk.Path,
                fused.Chunk.Language,
                fused.Chunk.StartLine,
                fused.Chunk.EndLine,
                fused.Chunk.Text,
                fused.Score,
                fused.LexicalRank,
                fused.VectorRank))
            .ToList();

        return new CodeSearchResult(snapshot.CommitSha, hits);
    }

    /// <summary>
    /// <c>read_file_range</c>: numbered lines of one indexed file. The end of the range is
    /// clamped to the end of the file; the returned range says what was actually read.
    /// </summary>
    public async Task<FileRange> ReadFileRangeAsync(
        Guid repositoryId,
        Guid snapshotId,
        string path,
        int startLine,
        int endLine,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new SourceToolException(SourceToolError.InvalidArgument, "path is required.");
        }

        if (startLine < 1 || endLine < startLine)
        {
            throw new SourceToolException(
                SourceToolError.InvalidArgument, "startLine must be 1 or greater and endLine must not precede it.");
        }

        if (endLine - startLine + 1 > MaxRangeLines)
        {
            throw new SourceToolException(
                SourceToolError.InvalidArgument, $"At most {MaxRangeLines} lines can be read per call.");
        }

        var snapshot = await SnapshotAccess.RequireCompletedAsync(reader, repositoryId, snapshotId, cancellationToken);
        var lastLine = (path.Contains('\0') ? null : await reader.FindLastLineAsync(snapshot.Id, path, cancellationToken))
            ?? throw new SourceToolException(
                SourceToolError.PathNotFound, "The path is not an indexed file in this snapshot.");

        if (startLine > lastLine)
        {
            throw new SourceToolException(
                SourceToolError.RangeOutOfBounds, $"The file has {lastLine} indexed lines.");
        }

        var clampedEnd = Math.Min(endLine, lastLine);
        var chunks = await reader.GetChunksInRangeAsync(snapshot.Id, path, startLine, clampedEnd, cancellationToken);

        return new FileRange(
            snapshot.CommitSha, path, startLine, clampedEnd, SourceLines.Assemble(chunks, startLine, clampedEnd));
    }
}

internal static class SourceLines
{
    /// <summary>
    /// Rebuilds numbered lines from chunks. Lines not covered by any chunk belong to
    /// whitespace-only regions the chunker did not store, and are returned empty.
    /// </summary>
    public static IReadOnlyList<SourceLine> Assemble(IReadOnlyList<ChunkRecord> chunks, int startLine, int endLine)
    {
        var lines = new SourceLine[endLine - startLine + 1];
        for (var number = startLine; number <= endLine; number++)
        {
            lines[number - startLine] = new SourceLine(number, string.Empty);
        }

        foreach (var chunk in chunks)
        {
            var chunkLines = chunk.Text.Split('\n');
            var from = Math.Max(startLine, chunk.StartLine);
            var to = Math.Min(endLine, chunk.EndLine);
            for (var number = from; number <= to; number++)
            {
                lines[number - startLine] = new SourceLine(number, chunkLines[number - chunk.StartLine]);
            }
        }

        return lines;
    }
}
