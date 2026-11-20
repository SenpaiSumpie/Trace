using System.Security.Cryptography;
using System.Text;

namespace Trace.Domain;

/// <summary>A contiguous range of lines from one file in one snapshot.</summary>
public sealed class SourceChunk : Entity
{
    public const int MaxPathLength = 1024;
    public const int MaxLanguageLength = 50;

    /// <summary>Upper bound on chunk text, in characters. Larger inputs must be split by the chunker.</summary>
    public const int MaxTextLength = 16_000;

    private SourceChunk()
    {
    }

    public Guid SnapshotId { get; private set; }

    /// <summary>Repository-relative path using forward slashes.</summary>
    public string Path { get; private set; } = null!;

    public string Language { get; private set; } = null!;

    /// <summary>1-based, inclusive.</summary>
    public int StartLine { get; private set; }

    /// <summary>1-based, inclusive.</summary>
    public int EndLine { get; private set; }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 encoded <see cref="Text"/>.</summary>
    public string ContentHash { get; private set; } = null!;

    public string Text { get; private set; } = null!;

    public static SourceChunk Create(
        IndexSnapshot snapshot,
        string path,
        string language,
        int startLine,
        int endLine,
        string text)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != SnapshotStatus.Building)
        {
            throw new InvalidOperationException("Chunks can only be added to a building snapshot.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(endLine, startLine);
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (text.Length > MaxTextLength)
        {
            throw new ArgumentException($"Must be at most {MaxTextLength} characters.", nameof(text));
        }

        return new SourceChunk
        {
            SnapshotId = snapshot.Id,
            Path = ValidatePath(path),
            Language = Guard.Required(language, MaxLanguageLength, nameof(language)),
            StartLine = startLine,
            EndLine = endLine,
            ContentHash = ComputeContentHash(text),
            Text = text,
        };
    }

    public static string ComputeContentHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string ValidatePath(string path)
    {
        Guard.Required(path, MaxPathLength, nameof(path));
        if (path.Contains('\\') || path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                "Must be a repository-relative path with forward slashes and no empty, '.' or '..' segments.",
                nameof(path));
        }

        return path;
    }
}
