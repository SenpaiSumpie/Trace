using Trace.Domain;

namespace Trace.UnitTests;

public class DomainRuleTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("0123456")]
    [InlineData("main")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("0123456789abcdef0123456789abcdef01234567\n")]
    public void Snapshot_rejects_anything_but_a_full_lowercase_commit_sha(string commitSha)
    {
        Assert.Throws<ArgumentException>(() => IndexSnapshot.Start(Guid.NewGuid(), commitSha, "main", "p", "m"));
    }

    [Fact]
    public void Snapshot_completes_once_with_a_utc_timestamp()
    {
        var snapshot = BuildingSnapshot();

        snapshot.Complete(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(-5)));

        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 17, 0, 0, TimeSpan.Zero), snapshot.CompletedAt);
        Assert.Equal(TimeSpan.Zero, snapshot.CompletedAt!.Value.Offset);
        Assert.Throws<InvalidOperationException>(() => snapshot.Complete(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(snapshot.Fail);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData("src\\App.cs")]
    [InlineData("src//App.cs")]
    [InlineData("./src/App.cs")]
    [InlineData("src/")]
    public void Chunk_rejects_paths_that_are_not_plain_repository_relative(string path)
    {
        Assert.Throws<ArgumentException>(() => SourceChunk.Create(BuildingSnapshot(), path, "csharp", 1, 1, "x"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    public void Chunk_rejects_invalid_line_ranges(int startLine, int endLine)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SourceChunk.Create(BuildingSnapshot(), "src/App.cs", "csharp", startLine, endLine, "x"));
    }

    [Fact]
    public void Chunk_rejects_text_over_the_cap()
    {
        var text = new string('x', SourceChunk.MaxTextLength + 1);

        Assert.Throws<ArgumentException>(() => SourceChunk.Create(BuildingSnapshot(), "src/App.cs", "csharp", 1, 1, text));
    }

    [Fact]
    public void Chunk_cannot_be_added_to_a_completed_snapshot()
    {
        Assert.Throws<InvalidOperationException>(
            () => SourceChunk.Create(CompletedSnapshot(), "src/App.cs", "csharp", 1, 1, "x"));
    }

    [Fact]
    public void Chunk_content_hash_is_the_sha256_of_its_utf8_text()
    {
        var chunk = SourceChunk.Create(BuildingSnapshot(), "src/App.cs", "csharp", 1, 1, "abc");

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", chunk.ContentHash);
    }

    [Fact]
    public void Investigation_pins_only_a_completed_snapshot()
    {
        var completed = CompletedSnapshot();

        var investigation = Investigation.Create(completed, InvestigationType.Question, "How does paging work?");

        Assert.Equal(completed.Id, investigation.SnapshotId);
        Assert.Equal(completed.RepositoryId, investigation.RepositoryId);
        Assert.Equal(InvestigationState.Queued, investigation.State);
        Assert.Throws<InvalidOperationException>(
            () => Investigation.Create(BuildingSnapshot(), InvestigationType.Question, "How does paging work?"));
    }

    [Fact]
    public void Evidence_must_come_from_the_pinned_snapshot()
    {
        var pinned = BuildingSnapshot();
        var pinnedChunk = SourceChunk.Create(pinned, "src/App.cs", "csharp", 1, 1, "x");
        pinned.Complete(DateTimeOffset.UtcNow);
        var foreignChunk = SourceChunk.Create(BuildingSnapshot(), "src/App.cs", "csharp", 1, 1, "x");
        var investigation = Investigation.Create(pinned, InvestigationType.Question, "q");

        var evidence = EvidenceRef.ForCode(investigation, pinnedChunk, "{}", "x");

        Assert.Equal(pinned.Id, evidence.SnapshotId);
        Assert.Equal(pinnedChunk.Id, evidence.ChunkId);
        Assert.Throws<InvalidOperationException>(() => EvidenceRef.ForCode(investigation, foreignChunk, "{}", "x"));
    }

    [Fact]
    public void Entities_get_distinct_time_ordered_ids()
    {
        var first = BuildingSnapshot();
        var second = BuildingSnapshot();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(7, first.Id.Version);
    }

    private static IndexSnapshot BuildingSnapshot() =>
        IndexSnapshot.Start(Guid.NewGuid(), Sha, "main", "text-v1", "fake-v1");

    private static IndexSnapshot CompletedSnapshot()
    {
        var snapshot = BuildingSnapshot();
        snapshot.Complete(DateTimeOffset.UtcNow);
        return snapshot;
    }
}
