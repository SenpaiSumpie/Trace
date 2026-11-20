namespace Trace.Application.Retrieval;

public sealed record FusedChunk(ChunkRecord Chunk, double Score, int? LexicalRank, int? VectorRank);

/// <summary>
/// Merges ranked lists by reciprocal rank fusion: each list contributes 1 / (K + rank) to a
/// chunk's score. Only ranks are used, so lexical and vector scores need no calibration.
/// </summary>
public static class ReciprocalRankFusion
{
    /// <summary>Standard damping constant; larger values flatten the advantage of top ranks.</summary>
    public const int K = 60;

    public static IReadOnlyList<FusedChunk> Fuse(
        IReadOnlyList<ChunkRecord> lexical,
        IReadOnlyList<ChunkRecord> vector,
        int limit)
    {
        var fused = new Dictionary<Guid, FusedChunk>();

        for (var index = 0; index < lexical.Count; index++)
        {
            var rank = index + 1;
            fused[lexical[index].Id] = new FusedChunk(lexical[index], 1.0 / (K + rank), rank, VectorRank: null);
        }

        for (var index = 0; index < vector.Count; index++)
        {
            var rank = index + 1;
            var contribution = 1.0 / (K + rank);
            fused[vector[index].Id] = fused.TryGetValue(vector[index].Id, out var existing)
                ? existing with { Score = existing.Score + contribution, VectorRank = rank }
                : new FusedChunk(vector[index], contribution, LexicalRank: null, rank);
        }

        // Path and line break ties so identical inputs always produce the identical order.
        return [.. fused.Values
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Chunk.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Chunk.StartLine)
            .Take(limit)];
    }
}
