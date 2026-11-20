namespace Trace.Application.Retrieval;

/// <summary>A claimed source location, e.g. as produced by a model.</summary>
public sealed record Citation(string Path, int StartLine, int EndLine);

public enum CitationProblem
{
    InvalidRange,
    RangeTooLarge,
    PathNotInSnapshot,
    RangeBeyondEndOfFile,
}

/// <summary>
/// The outcome for one citation. A valid citation carries the chunk containing its first line
/// and the exact cited text, both read from the snapshot rather than taken from the claim.
/// </summary>
public sealed record CitationCheck(Citation Citation, CitationProblem? Problem, Guid? ChunkId, string? Excerpt)
{
    public bool IsValid => Problem is null;
}

/// <summary>
/// Checks claimed citations against a pinned snapshot. A citation is accepted only if its path
/// is, character for character, an indexed file of that snapshot and its line range lies inside
/// that file. Nothing here touches the file system, so path tricks cannot reach outside the index.
/// </summary>
public sealed class CitationValidator(ISnapshotReader reader)
{
    public const int MaxCitations = 50;

    public async Task<IReadOnlyList<CitationCheck>> ValidateAsync(
        Guid repositoryId,
        Guid snapshotId,
        IReadOnlyList<Citation> citations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(citations);
        if (citations.Count > MaxCitations)
        {
            throw new SourceToolException(
                SourceToolError.InvalidArgument, $"At most {MaxCitations} citations can be validated per call.");
        }

        var snapshot = await SnapshotAccess.RequireCompletedAsync(reader, repositoryId, snapshotId, cancellationToken);

        var checks = new List<CitationCheck>(citations.Count);
        foreach (var citation in citations)
        {
            checks.Add(await CheckAsync(snapshot.Id, citation, cancellationToken));
        }

        return checks;
    }

    private async Task<CitationCheck> CheckAsync(Guid snapshotId, Citation citation, CancellationToken cancellationToken)
    {
        if (citation.StartLine < 1 || citation.EndLine < citation.StartLine)
        {
            return Rejected(citation, CitationProblem.InvalidRange);
        }

        if (citation.EndLine - citation.StartLine + 1 > SourceTools.MaxRangeLines)
        {
            return Rejected(citation, CitationProblem.RangeTooLarge);
        }

        if (string.IsNullOrEmpty(citation.Path)
            || citation.Path.Contains('\0')
            || await reader.FindLastLineAsync(snapshotId, citation.Path, cancellationToken) is not { } lastLine)
        {
            return Rejected(citation, CitationProblem.PathNotInSnapshot);
        }

        if (citation.EndLine > lastLine)
        {
            return Rejected(citation, CitationProblem.RangeBeyondEndOfFile);
        }

        var chunks = await reader.GetChunksInRangeAsync(
            snapshotId, citation.Path, citation.StartLine, citation.EndLine, cancellationToken);
        var lines = SourceLines.Assemble(chunks, citation.StartLine, citation.EndLine);
        var firstChunk = chunks.FirstOrDefault(
            chunk => chunk.StartLine <= citation.StartLine && chunk.EndLine >= citation.StartLine)
            ?? chunks.FirstOrDefault();

        // A range that touches no stored chunk lies entirely in whitespace: there is nothing to cite.
        return firstChunk is null
            ? Rejected(citation, CitationProblem.InvalidRange)
            : new CitationCheck(citation, Problem: null, firstChunk.Id, string.Join('\n', lines.Select(line => line.Text)));
    }

    private static CitationCheck Rejected(Citation citation, CitationProblem problem) =>
        new(citation, problem, ChunkId: null, Excerpt: null);
}
