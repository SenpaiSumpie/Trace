namespace Trace.Application.Indexing;

/// <summary>An indexable file: repository-relative path, language, and text with <c>\n</c> line endings.</summary>
public sealed record SourceFile(string Path, string Language, string Text);

public sealed record SkippedFile(string Path, SkipReason Reason);

/// <summary>The indexable content of a repository at one exact revision.</summary>
public sealed record SourceRevision(
    string CommitSha,
    string Branch,
    IReadOnlyList<SourceFile> Files,
    IReadOnlyList<SkippedFile> Skipped);

/// <summary>Reads a repository's indexable content. Implementations enforce <see cref="SourceFilePolicy"/>.</summary>
public interface IRepositorySource
{
    Task<SourceRevision> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a repository exceeds an indexing limit; nothing is indexed.</summary>
public sealed class SourceLimitExceededException(string message) : Exception(message);
