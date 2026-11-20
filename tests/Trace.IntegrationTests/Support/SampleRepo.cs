using Trace.Infrastructure.Indexing;

namespace Trace.IntegrationTests.Support;

/// <summary>The checked-in fixture repository at <c>tests/fixtures/sample-repo</c>.</summary>
internal static class SampleRepo
{
    public static string Path { get; } = Locate();

    public static LocalDirectorySource Source(string? directory = null) => new(directory ?? Path, "main");

    /// <summary>A file's lines as the indexer sees them: <c>\n</c> line endings, no trailing empty line.</summary>
    public static string[] ReadLines(string relativePath, string? directory = null)
    {
        var text = File.ReadAllText(System.IO.Path.Combine(directory ?? Path, relativePath))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>Copies the fixture to a temporary directory that is deleted on dispose.</summary>
    public static TempDirectory CopyToTemp()
    {
        var temp = new TempDirectory();
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            var target = System.IO.Path.Combine(temp.Path, System.IO.Path.GetRelativePath(Path, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return temp;
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Trace.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Trace.slnx above the test output directory.");
        return System.IO.Path.Combine(root, "tests", "fixtures", "sample-repo");
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"trace-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
