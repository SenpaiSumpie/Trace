using Trace.Domain;

namespace Trace.Application.Indexing;

public sealed record ChunkDraft(string Path, string Language, int StartLine, int EndLine, string Text);

/// <summary>
/// Text-first chunker: consecutive, non-overlapping windows of whole lines. Deterministic, and
/// every chunk's text is exactly lines <c>StartLine..EndLine</c> of the file joined with <c>\n</c>.
/// </summary>
public static class LineChunker
{
    /// <summary>Recorded on each snapshot; change it whenever chunk boundaries would change.</summary>
    public const string Version = "lines-v1";

    public const int MaxLines = 40;

    public static IReadOnlyList<ChunkDraft> Chunk(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var lines = file.Text.Split('\n');
        var lineCount = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        var drafts = new List<ChunkDraft>();
        var start = 0;
        while (start < lineCount)
        {
            var end = start;
            var length = lines[start].Length;
            while (end + 1 < lineCount
                && end + 1 - start < MaxLines
                && length + 1 + lines[end + 1].Length <= SourceChunk.MaxTextLength)
            {
                end++;
                length += 1 + lines[end].Length;
            }

            var text = string.Join('\n', lines, start, end - start + 1);
            if (!string.IsNullOrWhiteSpace(text))
            {
                drafts.Add(new ChunkDraft(file.Path, file.Language, start + 1, end + 1, text));
            }

            start = end + 1;
        }

        return drafts;
    }
}
