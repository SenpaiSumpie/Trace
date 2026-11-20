using Trace.Application.Indexing;
using Trace.Infrastructure.Indexing;

namespace Trace.SecurityTests;

/// <summary>Repository content is untrusted: the reader must stay inside the allowlist and its limits.</summary>
public sealed class LocalDirectorySourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"trace-test-{Guid.NewGuid():N}");

    public LocalDirectorySourceTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task Only_allowlisted_files_outside_excluded_directories_are_read()
    {
        Write("src/App.cs", "class App {}\n");
        Write("README.md", "# Readme\n");
        Write(".env", "API_KEY=super-secret\n");
        Write("deploy/server.pem", "-----BEGIN PRIVATE KEY-----\n");
        Write("id_rsa", "-----BEGIN OPENSSH PRIVATE KEY-----\n");
        Write(".git/config.json", "{}\n");
        Write(".github/workflows/ci.yml", "on: push\n");
        Write("node_modules/left-pad/index.js", "module.exports = 1\n");
        Write("src/bin/Debug/Generated.cs", "class Generated {}\n");
        Write("package-lock.json", "{}\n");

        var revision = await ReadAsync();

        Assert.Equal(["README.md", "src/App.cs"], revision.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.Empty(revision.Skipped);
        Assert.DoesNotContain(revision.Files, file => file.Text.Contains("super-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_content_is_skipped_with_a_reason_and_the_rest_is_indexed()
    {
        Write("src/App.cs", "class App {}\n");
        File.WriteAllBytes(Path.Combine(root, "binary.cs"), [0x4D, 0x5A, 0x00, 0x00]);
        File.WriteAllBytes(Path.Combine(root, "latin1.cs"), [0x2F, 0x2F, 0x20, 0xE9, 0x0A]);
        Write("huge.md", new string('a', SourceFilePolicy.MaxFileBytes + 1));
        Write("minified.js", new string('x', SourceFilePolicy.MaxLineLength + 1));
        Write("empty.ts", "\n\n");

        var revision = await ReadAsync();

        Assert.Equal("src/App.cs", Assert.Single(revision.Files).Path);
        Assert.Equal(
            new Dictionary<string, SkipReason>
            {
                ["binary.cs"] = SkipReason.Binary,
                ["latin1.cs"] = SkipReason.InvalidEncoding,
                ["huge.md"] = SkipReason.TooLarge,
                ["minified.js"] = SkipReason.LineTooLong,
                ["empty.ts"] = SkipReason.Empty,
            },
            revision.Skipped.ToDictionary(skipped => skipped.Path, skipped => skipped.Reason));
    }

    [Fact]
    public async Task Symbolic_links_are_not_followed()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"trace-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "Secret.cs"), "// outside the repository\n");
            Write("src/App.cs", "class App {}\n");
            try
            {
                File.CreateSymbolicLink(Path.Combine(root, "Linked.cs"), Path.Combine(outside, "Secret.cs"));
                Directory.CreateSymbolicLink(Path.Combine(root, "linked-dir"), outside);
            }
            catch (IOException)
            {
                // Windows without Developer Mode cannot create symlinks; CI (Linux) exercises this.
                return;
            }

            var revision = await ReadAsync();

            Assert.Equal("src/App.cs", Assert.Single(revision.Files).Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(root);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Too_many_files_fails_the_read()
    {
        for (var i = 0; i <= SourceFilePolicy.MaxFiles; i++)
        {
            Write($"src/File{i}.cs", "class C {}\n");
        }

        await Assert.ThrowsAsync<SourceLimitExceededException>(ReadAsync);
    }

    [Fact]
    public async Task Too_deep_nesting_fails_the_read()
    {
        var nested = string.Join('/', Enumerable.Repeat("d", SourceFilePolicy.MaxDirectoryDepth + 1));
        Write($"{nested}/Deep.cs", "class Deep {}\n");

        await Assert.ThrowsAsync<SourceLimitExceededException>(ReadAsync);
    }

    [Fact]
    public async Task Too_much_content_fails_the_read()
    {
        var line = new string('a', 99) + "\n";
        var file = string.Concat(Enumerable.Repeat(line, SourceFilePolicy.MaxFileBytes / line.Length));
        var filesNeeded = (int)(SourceFilePolicy.MaxTotalBytes / file.Length) + 1;
        for (var i = 0; i < filesNeeded; i++)
        {
            Write($"docs/big{i}.md", file);
        }

        await Assert.ThrowsAsync<SourceLimitExceededException>(ReadAsync);
    }

    [Fact]
    public async Task Revision_id_depends_only_on_indexed_content()
    {
        Write("src/App.cs", "class App {}\n");
        var before = (await ReadAsync()).CommitSha;

        Write(".env", "API_KEY=changed\n");
        Write("notes.txt", "not indexed\n");
        var afterIgnoredChanges = (await ReadAsync()).CommitSha;

        Write("src/App.cs", "class App { }\n");
        var afterContentChange = (await ReadAsync()).CommitSha;

        Assert.Matches("^[0-9a-f]{40}$", before);
        Assert.Equal(before, afterIgnoredChanges);
        Assert.NotEqual(before, afterContentChange);
    }

    [Fact]
    public async Task Renaming_a_file_changes_the_revision_id()
    {
        Write("src/App.cs", "class App {}\n");
        var before = (await ReadAsync()).CommitSha;

        File.Move(Path.Combine(root, "src", "App.cs"), Path.Combine(root, "src", "Main.cs"));

        Assert.NotEqual(before, (await ReadAsync()).CommitSha);
    }

    private Task<SourceRevision> ReadAsync() => new LocalDirectorySource(root, "main").ReadAsync();

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
