using System.Collections.Frozen;

namespace Trace.Application.Indexing;

/// <summary>
/// Decides which repository files may be indexed and the hard limits on a single index run.
/// Allowlist-based: a file is indexed only if its extension is known source or documentation.
/// </summary>
public static class SourceFilePolicy
{
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxFiles = 2_000;
    public const long MaxTotalBytes = 20 * 1024 * 1024;
    public const int MaxDirectoryDepth = 20;

    /// <summary>Longer lines indicate minified or generated content, which is not indexed.</summary>
    public const int MaxLineLength = 2_000;

    private static readonly FrozenDictionary<string, string> LanguagesByExtension =
        new Dictionary<string, string>
        {
            [".cs"] = "csharp",
            [".ts"] = "typescript",
            [".tsx"] = "typescript",
            [".js"] = "javascript",
            [".jsx"] = "javascript",
            [".md"] = "markdown",
            [".json"] = "json",
            [".yml"] = "yaml",
            [".yaml"] = "yaml",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ExcludedDirectories = FrozenSet.ToFrozenSet(
        ["node_modules", "bin", "obj", "dist", "build", "out", "vendor", "coverage", "packages"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ExcludedFileNames = FrozenSet.ToFrozenSet(
        ["package-lock.json", "npm-shrinkwrap.json"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ExcludedSuffixes = [".min.js", ".g.cs", ".designer.cs", ".d.ts"];

    /// <summary>Dot-directories (<c>.git</c>, <c>.github</c>, ...) and dependency/build output are never entered.</summary>
    public static bool IsExcludedDirectory(string name) =>
        name.StartsWith('.') || ExcludedDirectories.Contains(name);

    /// <summary>The language of an indexable file, or null if the file must not be indexed.</summary>
    public static string? LanguageFor(string fileName)
    {
        if (fileName.StartsWith('.')
            || ExcludedFileNames.Contains(fileName)
            || ExcludedSuffixes.Any(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return LanguagesByExtension.GetValueOrDefault(Path.GetExtension(fileName));
    }
}
