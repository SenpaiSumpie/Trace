using Microsoft.EntityFrameworkCore;
using Trace.Application.Indexing;
using Trace.Domain;
using Trace.Infrastructure.Embeddings;
using Trace.Infrastructure.Persistence;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class FixtureIndexingTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>
    /// Content-derived revision ID of the checked-in fixture. It must be identical on every
    /// platform; update it deliberately whenever the fixture's indexed files change.
    /// </summary>
    private const string FixtureRevision = "ee02b2da366aa000b30541463014b895d5cebb67";

    private TestDatabase database = null!;
    private TraceDbContext db = null!;
    private Repository repository = null!;

    public async Task InitializeAsync()
    {
        database = await postgres.CreateDatabaseAsync();
        db = database.CreateContext();
        repository = await TestGraph.AddRepositoryAsync(db);
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task Fixture_is_published_as_a_completed_snapshot_at_its_known_revision()
    {
        var result = await database.IndexAsync(repository.Id, SampleRepo.Source());

        var snapshot = await db.IndexSnapshots.SingleAsync();
        Assert.Equal(IndexOutcome.Indexed, result.Outcome);
        Assert.Equal(snapshot.Id, result.SnapshotId);
        Assert.Equal(FixtureRevision, result.CommitSha);
        Assert.Equal(FixtureRevision, snapshot.CommitSha);
        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
        Assert.NotNull(snapshot.CompletedAt);
        Assert.Equal("main", snapshot.Branch);
        Assert.Equal(LineChunker.Version, snapshot.ParserVersion);
        Assert.Equal("fake-hash-v1", snapshot.EmbeddingModel);
        Assert.Equal(result.ChunkCount, await db.SourceChunks.CountAsync());
    }

    [Fact]
    public async Task Every_chunk_is_exactly_its_cited_lines_of_the_file_on_disk()
    {
        await database.IndexAsync(repository.Id, SampleRepo.Source());

        var chunks = await db.SourceChunks.AsNoTracking().ToListAsync();
        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            var lines = SampleRepo.ReadLines(chunk.Path);
            var cited = string.Join('\n', lines[(chunk.StartLine - 1)..chunk.EndLine]);

            Assert.Equal(cited, chunk.Text);
            Assert.Equal(SourceChunk.ComputeContentHash(cited), chunk.ContentHash);
        }
    }

    [Fact]
    public async Task Chunks_cover_each_indexed_file_from_first_line_to_last_without_gaps_or_overlap()
    {
        await database.IndexAsync(repository.Id, SampleRepo.Source());

        var byPath = (await db.SourceChunks.AsNoTracking().ToListAsync()).GroupBy(chunk => chunk.Path);
        foreach (var file in byPath)
        {
            var ordered = file.OrderBy(chunk => chunk.StartLine).ToList();

            Assert.Equal(1, ordered[0].StartLine);
            Assert.Equal(SampleRepo.ReadLines(file.Key).Length, ordered[^1].EndLine);
            Assert.All(ordered.Zip(ordered.Skip(1)), pair => Assert.Equal(pair.First.EndLine + 1, pair.Second.StartLine));
            Assert.All(ordered, chunk => Assert.InRange(chunk.EndLine - chunk.StartLine + 1, 1, LineChunker.MaxLines));
        }
    }

    [Fact]
    public async Task Known_source_lines_are_found_at_their_real_line_numbers()
    {
        await database.IndexAsync(repository.Id, SampleRepo.Source());

        // The deliberate off-by-one in the fixture's pagination.
        Assert.Equal(
            "            var skip = request.Page * request.PageSize;",
            await LineAsync("src/TaskBoard.Api/Tasks/TaskRepository.cs", 25));

        // The first line of the second chunk of a file longer than one chunk.
        Assert.Equal(
            "    <section>",
            await LineAsync("web/src/TaskList.tsx", 41));
    }

    [Fact]
    public async Task Indexed_files_are_exactly_the_allowlisted_fixture_files_with_languages()
    {
        await database.IndexAsync(repository.Id, SampleRepo.Source());

        var languages = await db.SourceChunks
            .Select(chunk => new { chunk.Path, chunk.Language })
            .Distinct()
            .ToDictionaryAsync(file => file.Path, file => file.Language);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["README.md"] = "markdown",
                ["docs/CONTRIBUTING.md"] = "markdown",
                ["docs/adr/0001-page-size-cap.md"] = "markdown",
                ["src/TaskBoard.Api/Auth/ApiKeyMiddleware.cs"] = "csharp",
                ["src/TaskBoard.Api/Program.cs"] = "csharp",
                ["src/TaskBoard.Api/Reports/ReportQueue.cs"] = "csharp",
                ["src/TaskBoard.Api/Reports/ReportWorker.cs"] = "csharp",
                ["src/TaskBoard.Api/Tasks/PageRequest.cs"] = "csharp",
                ["src/TaskBoard.Api/Tasks/TaskItem.cs"] = "csharp",
                ["src/TaskBoard.Api/Tasks/TaskRepository.cs"] = "csharp",
                ["src/TaskBoard.Api/appsettings.json"] = "json",
                ["tests/TaskBoard.Tests/PageRequestTests.cs"] = "csharp",
                ["web/src/TaskList.tsx"] = "typescript",
                ["web/src/api.ts"] = "typescript",
            },
            languages);
    }

    [Fact]
    public async Task Stored_embeddings_are_the_providers_vectors_for_the_chunk_text()
    {
        await database.IndexAsync(repository.Id, SampleRepo.Source());

        var chunks = await db.SourceChunks.AsNoTracking().ToListAsync();
        var expected = await new DeterministicFakeEmbeddingProvider().EmbedAsync([.. chunks.Select(chunk => chunk.Text)]);

        Assert.All(chunks.Zip(expected), pair => Assert.Equal(pair.Second, pair.First.Embedding));
    }

    [Fact]
    public async Task Indexing_is_reproducible_across_databases()
    {
        await using var otherDatabase = await postgres.CreateDatabaseAsync();
        await using var otherDb = otherDatabase.CreateContext();
        var otherRepository = await TestGraph.AddRepositoryAsync(otherDb);

        var first = await database.IndexAsync(repository.Id, SampleRepo.Source());
        var second = await otherDatabase.IndexAsync(otherRepository.Id, SampleRepo.Source());

        Assert.Equal(first.CommitSha, second.CommitSha);
        Assert.Equal(await FingerprintAsync(db), await FingerprintAsync(otherDb));
    }

    [Fact]
    public async Task Indexing_the_same_content_again_is_idempotent()
    {
        var first = await database.IndexAsync(repository.Id, SampleRepo.Source());
        var fingerprint = await FingerprintAsync(db);

        var second = await database.IndexAsync(repository.Id, SampleRepo.Source());

        Assert.Equal(IndexOutcome.AlreadyIndexed, second.Outcome);
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.ChunkCount, second.ChunkCount);
        Assert.Equal(1, await db.IndexSnapshots.CountAsync());
        Assert.Equal(fingerprint, await FingerprintAsync(db));
    }

    [Fact]
    public async Task Changed_content_gets_a_new_snapshot_and_leaves_the_published_one_untouched()
    {
        using var copy = SampleRepo.CopyToTemp();
        var original = await database.IndexAsync(repository.Id, SampleRepo.Source(copy.Path));
        var originalFingerprint = await FingerprintAsync(db, original.SnapshotId);

        await File.AppendAllTextAsync(Path.Combine(copy.Path, "README.md"), "\n## Changelog\n\nAdded a section.\n");
        var changed = await database.IndexAsync(repository.Id, SampleRepo.Source(copy.Path));

        Assert.Equal(IndexOutcome.Indexed, changed.Outcome);
        Assert.NotEqual(original.CommitSha, changed.CommitSha);
        Assert.NotEqual(original.SnapshotId, changed.SnapshotId);
        Assert.Equal(2, await db.IndexSnapshots.CountAsync(snapshot => snapshot.Status == SnapshotStatus.Completed));
        Assert.Equal(originalFingerprint, await FingerprintAsync(db, original.SnapshotId));
    }

    [Fact]
    public async Task Line_endings_do_not_change_the_revision_or_the_chunks()
    {
        using var copy = SampleRepo.CopyToTemp();
        foreach (var file in Directory.EnumerateFiles(copy.Path, "*", SearchOption.AllDirectories))
        {
            var unix = (await File.ReadAllTextAsync(file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, unix.Replace("\n", "\r\n", StringComparison.Ordinal));
        }

        var windows = await database.IndexAsync(repository.Id, SampleRepo.Source(copy.Path));

        Assert.Equal(FixtureRevision, windows.CommitSha);
        Assert.False(await db.SourceChunks.AnyAsync(chunk => chunk.Text.Contains("\r")));
    }

    [Fact]
    public async Task Failed_run_publishes_nothing_and_the_same_revision_can_be_indexed_again()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.IndexAsync(repository.Id, SampleRepo.Source(), new FailingEmbeddingProvider()));
        Assert.Equal("embedding service unavailable", failure.Message);

        var failed = await db.IndexSnapshots.AsNoTracking().SingleAsync();
        Assert.Equal(SnapshotStatus.Failed, failed.Status);
        Assert.Null(failed.CompletedAt);

        var retry = await database.IndexAsync(repository.Id, SampleRepo.Source(), new FailingEmbeddingProvider(failures: 0));

        Assert.Equal(IndexOutcome.Indexed, retry.Outcome);
        Assert.NotEqual(failed.Id, retry.SnapshotId);
        Assert.Equal(1, await db.IndexSnapshots.CountAsync(snapshot => snapshot.Status == SnapshotStatus.Completed));
    }

    [Fact]
    public async Task Run_fails_when_the_provider_returns_the_wrong_number_of_vectors()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.IndexAsync(repository.Id, SampleRepo.Source(), new ShortEmbeddingProvider()));

        Assert.Equal(SnapshotStatus.Failed, (await db.IndexSnapshots.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Run_does_nothing_while_another_run_is_building_the_same_snapshot()
    {
        var building = IndexSnapshot.Start(
            repository.Id, FixtureRevision, "main", LineChunker.Version, new DeterministicFakeEmbeddingProvider().ModelId);
        db.Add(building);
        await db.SaveChangesAsync();

        var result = await database.IndexAsync(repository.Id, SampleRepo.Source());

        Assert.Equal(IndexOutcome.InProgress, result.Outcome);
        Assert.Equal(building.Id, result.SnapshotId);
        Assert.Equal(0, await db.SourceChunks.CountAsync());
        Assert.Equal(SnapshotStatus.Building, (await db.IndexSnapshots.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Missing_repository_directory_fails_before_anything_is_stored()
    {
        var missing = Path.Combine(SampleRepo.Path, "does-not-exist");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => database.IndexAsync(repository.Id, SampleRepo.Source(missing)));

        Assert.Equal(0, await db.IndexSnapshots.CountAsync());
    }

    private async Task<string> LineAsync(string path, int lineNumber)
    {
        var chunk = await db.SourceChunks.AsNoTracking().SingleAsync(
            c => c.Path == path && c.StartLine <= lineNumber && c.EndLine >= lineNumber);
        return chunk.Text.Split('\n')[lineNumber - chunk.StartLine];
    }

    /// <summary>Everything that identifies indexed content, independent of generated IDs and timestamps.</summary>
    private static async Task<List<string>> FingerprintAsync(TraceDbContext context, Guid? snapshotId = null)
    {
        var chunks = await context.SourceChunks.AsNoTracking()
            .Where(chunk => snapshotId == null || chunk.SnapshotId == snapshotId)
            .OrderBy(chunk => chunk.Path).ThenBy(chunk => chunk.StartLine)
            .ToListAsync();

        return [.. chunks.Select(chunk =>
            $"{chunk.Path}:{chunk.StartLine}-{chunk.EndLine}:{chunk.Language}:{chunk.ContentHash}:"
            + SourceChunk.ComputeContentHash(string.Join(',', chunk.Embedding.Select(value => value.ToString("R")))))];
    }

    private sealed class FailingEmbeddingProvider(int failures = 1) : IEmbeddingProvider
    {
        private readonly DeterministicFakeEmbeddingProvider inner = new();
        private int remainingFailures = failures;

        public string ModelId => inner.ModelId;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            remainingFailures-- > 0
                ? throw new InvalidOperationException("embedding service unavailable")
                : inner.EmbedAsync(texts, cancellationToken);
    }

    private sealed class ShortEmbeddingProvider : IEmbeddingProvider
    {
        public string ModelId => "short-v1";

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<float[]>>([new float[SourceChunk.EmbeddingDimensions]]);
    }
}
