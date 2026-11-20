using System.Security.Cryptography;
using System.Text;
using Trace.Application.Indexing;

namespace Trace.Infrastructure.Indexing;

/// <summary>
/// Reads a repository from a local directory (the demo fixture). The directory has no commits,
/// so the revision is identified by a content-derived SHA-1 over the indexed files: identical
/// content always produces the identical 40-character ID, and any change produces a new one.
/// Symbolic links are never followed.
/// </summary>
public sealed class LocalDirectorySource(string rootDirectory, string branch) : IRepositorySource
{
    public async Task<SourceRevision> ReadAsync(CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(rootDirectory);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"Repository directory '{root.FullName}' does not exist.");
        }

        var candidates = new List<(string Path, string Language, FileInfo File)>();
        Collect(root, relativePath: string.Empty, depth: 0, candidates);

        var files = new List<SourceFile>();
        var skipped = new List<SkippedFile>();
        long totalBytes = 0;
        foreach (var (path, language, file) in candidates)
        {
            if (file.Length > SourceFilePolicy.MaxFileBytes)
            {
                skipped.Add(new SkippedFile(path, SkipReason.TooLarge));
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
            totalBytes += bytes.Length;
            if (totalBytes > SourceFilePolicy.MaxTotalBytes)
            {
                throw new SourceLimitExceededException(
                    $"Repository exceeds the {SourceFilePolicy.MaxTotalBytes}-byte indexing limit.");
            }

            if (SourceText.TryDecode(bytes, out var text, out var skipReason))
            {
                files.Add(new SourceFile(path, language, text));
            }
            else
            {
                skipped.Add(new SkippedFile(path, skipReason));
            }
        }

        return new SourceRevision(ComputeRevisionId(files), branch, files, skipped);
    }

    private static void Collect(
        DirectoryInfo directory,
        string relativePath,
        int depth,
        List<(string Path, string Language, FileInfo File)> candidates)
    {
        if (depth > SourceFilePolicy.MaxDirectoryDepth)
        {
            throw new SourceLimitExceededException(
                $"Repository is nested deeper than {SourceFilePolicy.MaxDirectoryDepth} directories.");
        }

        // Ordinal ordering keeps the file list, and therefore the revision ID, platform-independent.
        foreach (var file in directory.EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (file.LinkTarget is not null || SourceFilePolicy.LanguageFor(file.Name) is not { } language)
            {
                continue;
            }

            if (candidates.Count >= SourceFilePolicy.MaxFiles)
            {
                throw new SourceLimitExceededException(
                    $"Repository has more than {SourceFilePolicy.MaxFiles} indexable files.");
            }

            candidates.Add((relativePath + file.Name, language, file));
        }

        foreach (var child in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (child.LinkTarget is not null || SourceFilePolicy.IsExcludedDirectory(child.Name))
            {
                continue;
            }

            Collect(child, $"{relativePath}{child.Name}/", depth + 1, candidates);
        }
    }

    private static string ComputeRevisionId(List<SourceFile> files)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            sha1.AppendData(Encoding.UTF8.GetBytes(file.Path));
            sha1.AppendData([0]);
            sha1.AppendData(SHA256.HashData(Encoding.UTF8.GetBytes(file.Text)));
        }

        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }
}
