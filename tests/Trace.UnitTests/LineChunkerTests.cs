using Trace.Application.Indexing;
using Trace.Domain;

namespace Trace.UnitTests;

public class LineChunkerTests
{
    [Fact]
    public void Short_file_is_one_chunk_with_exact_line_numbers()
    {
        var chunk = Assert.Single(Chunk("first\nsecond\nthird\n"));

        Assert.Equal(("src/App.cs", "csharp", 1, 3), (chunk.Path, chunk.Language, chunk.StartLine, chunk.EndLine));
        Assert.Equal("first\nsecond\nthird", chunk.Text);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\nb\n")]
    public void Trailing_newline_does_not_add_a_line(string text)
    {
        Assert.Equal(2, Assert.Single(Chunk(text)).EndLine);
    }

    [Fact]
    public void Long_file_is_split_into_consecutive_windows()
    {
        var lines = Enumerable.Range(1, 95).Select(number => $"line {number}").ToArray();

        var chunks = Chunk(string.Join('\n', lines));

        Assert.Equal([(1, 40), (41, 80), (81, 95)], chunks.Select(chunk => (chunk.StartLine, chunk.EndLine)));
        Assert.All(chunks, chunk =>
            Assert.Equal(string.Join('\n', lines[(chunk.StartLine - 1)..chunk.EndLine]), chunk.Text));
    }

    [Fact]
    public void Blank_lines_inside_a_chunk_are_preserved_so_line_numbers_stay_true()
    {
        var chunk = Assert.Single(Chunk("a\n\n\nb\n"));

        Assert.Equal("a\n\n\nb", chunk.Text);
        Assert.Equal(4, chunk.EndLine);
    }

    [Fact]
    public void Windows_containing_only_whitespace_are_not_emitted()
    {
        var text = string.Join('\n', Enumerable.Repeat(string.Empty, LineChunker.MaxLines)) + "\ncode\n";

        var chunk = Assert.Single(Chunk(text));

        Assert.Equal((LineChunker.MaxLines + 1, LineChunker.MaxLines + 1), (chunk.StartLine, chunk.EndLine));
        Assert.Equal("code", chunk.Text);
    }

    [Fact]
    public void Chunk_closes_early_rather_than_exceed_the_text_cap()
    {
        var longLine = new string('x', SourceFilePolicy.MaxLineLength);
        var lines = Enumerable.Repeat(longLine, 20).ToArray();

        var chunks = Chunk(string.Join('\n', lines));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, SourceChunk.MaxTextLength));
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(20, chunks[^1].EndLine);
        Assert.All(chunks.Zip(chunks.Skip(1)), pair => Assert.Equal(pair.First.EndLine + 1, pair.Second.StartLine));
    }

    [Fact]
    public void Chunking_is_deterministic()
    {
        var text = string.Join('\n', Enumerable.Range(1, 200).Select(number => $"line {number}"));

        Assert.Equal(Chunk(text), Chunk(text));
    }

    private static IReadOnlyList<ChunkDraft> Chunk(string text) =>
        LineChunker.Chunk(new SourceFile("src/App.cs", "csharp", text));
}
