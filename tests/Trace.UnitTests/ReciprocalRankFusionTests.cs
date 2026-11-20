using Trace.Application.Retrieval;

namespace Trace.UnitTests;

public class ReciprocalRankFusionTests
{
    private static readonly ChunkRecord A = Chunk("a.cs", 1);
    private static readonly ChunkRecord B = Chunk("b.cs", 1);
    private static readonly ChunkRecord C = Chunk("c.cs", 1);
    private static readonly ChunkRecord D = Chunk("d.cs", 1);

    [Fact]
    public void Chunk_found_by_both_retrievers_outranks_the_top_of_either_alone()
    {
        var fused = ReciprocalRankFusion.Fuse(lexical: [A, B], vector: [C, B], limit: 10);

        Assert.Equal([B, A, C], fused.Select(entry => entry.Chunk));
        Assert.Equal((2, 2), (fused[0].LexicalRank, fused[0].VectorRank));
        Assert.Equal((1, null), (fused[1].LexicalRank, fused[1].VectorRank));
        Assert.Equal((null, 1), (fused[2].LexicalRank, fused[2].VectorRank));
    }

    [Fact]
    public void Score_is_the_sum_of_reciprocal_ranks()
    {
        var fused = ReciprocalRankFusion.Fuse(lexical: [A, B], vector: [B], limit: 10);

        Assert.Equal((1.0 / 62) + (1.0 / 61), fused[0].Score, precision: 12);
        Assert.Equal(1.0 / 61, fused[1].Score, precision: 12);
    }

    [Fact]
    public void Ties_are_broken_by_path_then_line_so_order_is_stable()
    {
        var early = Chunk("same.cs", 1);
        var late = Chunk("same.cs", 41);

        var fused = ReciprocalRankFusion.Fuse(lexical: [D, late], vector: [A, early], limit: 10);

        Assert.Equal([A, D, early, late], fused.Select(entry => entry.Chunk));
    }

    [Fact]
    public void Result_is_limited_after_fusion()
    {
        var fused = ReciprocalRankFusion.Fuse(lexical: [A, B, C], vector: [D, C], limit: 2);

        Assert.Equal(2, fused.Count);
        Assert.Equal(C, fused[0].Chunk);
    }

    [Fact]
    public void One_empty_list_leaves_the_other_order_unchanged()
    {
        Assert.Equal([A, B, C], ReciprocalRankFusion.Fuse([A, B, C], [], 10).Select(entry => entry.Chunk));
        Assert.Equal([A, B, C], ReciprocalRankFusion.Fuse([], [A, B, C], 10).Select(entry => entry.Chunk));
        Assert.Empty(ReciprocalRankFusion.Fuse([], [], 10));
    }

    private static ChunkRecord Chunk(string path, int startLine) =>
        new(Guid.NewGuid(), path, "csharp", startLine, startLine + 39, "text");
}
