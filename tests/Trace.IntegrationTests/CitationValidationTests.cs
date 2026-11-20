using Microsoft.Extensions.DependencyInjection;
using Trace.Application.Indexing;
using Trace.Application.Retrieval;
using Trace.Domain;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>
/// A citation is a claim, typically from a model, and is never trusted: it is accepted only if
/// it resolves inside the pinned snapshot.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CitationValidationTests(IndexedSampleRepo indexed) : IClassFixture<IndexedSampleRepo>
{
    private const string Readme = "README.md";

    [Fact]
    public async Task Real_citation_is_accepted_with_the_text_read_from_the_snapshot()
    {
        var check = Assert.Single(await indexed.ValidateAsync(
            new Citation("src/TaskBoard.Api/Tasks/TaskRepository.cs", 24, 26)));

        var lines = SampleRepo.ReadLines("src/TaskBoard.Api/Tasks/TaskRepository.cs");
        Assert.True(check.IsValid);
        Assert.Equal(string.Join('\n', lines[23..26]), check.Excerpt);
        Assert.Contains("request.Page * request.PageSize", check.Excerpt, StringComparison.Ordinal);
        Assert.NotNull(check.ChunkId);
    }

    [Fact]
    public async Task Citation_spanning_two_chunks_is_accepted_and_anchored_to_the_chunk_of_its_first_line()
    {
        var check = Assert.Single(await indexed.ValidateAsync(new Citation("web/src/TaskList.tsx", 38, 45)));

        await using var scope = indexed.Database.CreateScope();
        var firstChunk = (await scope.ServiceProvider.GetRequiredService<ISnapshotReader>()
            .GetChunksInRangeAsync(indexed.SnapshotId, "web/src/TaskList.tsx", 38, 38))[0];
        Assert.True(check.IsValid);
        Assert.Equal(firstChunk.Id, check.ChunkId);
        Assert.Equal(string.Join('\n', SampleRepo.ReadLines("web/src/TaskList.tsx")[37..45]), check.Excerpt);
    }

    [Theory]
    [InlineData("src/TaskBoard.Api/Tasks/TaskService.cs", 1, 10, CitationProblem.PathNotInSnapshot)]
    [InlineData("../../../etc/passwd", 1, 1, CitationProblem.PathNotInSnapshot)]
    [InlineData("/etc/passwd", 1, 1, CitationProblem.PathNotInSnapshot)]
    [InlineData("C:\\Windows\\win.ini", 1, 1, CitationProblem.PathNotInSnapshot)]
    [InlineData("docs/../README.md", 1, 3, CitationProblem.PathNotInSnapshot)]
    [InlineData("./README.md", 1, 3, CitationProblem.PathNotInSnapshot)]
    [InlineData("readme.md", 1, 3, CitationProblem.PathNotInSnapshot)]
    [InlineData("README.md ", 1, 3, CitationProblem.PathNotInSnapshot)]
    [InlineData("src/TaskBoard.Api/Tasks", 1, 1, CitationProblem.PathNotInSnapshot)]
    [InlineData("README.md' OR '1'='1", 1, 1, CitationProblem.PathNotInSnapshot)]
    [InlineData("%", 1, 1, CitationProblem.PathNotInSnapshot)]
    [InlineData("README.md\0.cs", 1, 1, CitationProblem.PathNotInSnapshot)]
    [InlineData("", 1, 1, CitationProblem.PathNotInSnapshot)]
    [InlineData(Readme, 20, 60, CitationProblem.RangeBeyondEndOfFile)]
    [InlineData(Readme, 29, 29, CitationProblem.RangeBeyondEndOfFile)]
    [InlineData(Readme, 0, 3, CitationProblem.InvalidRange)]
    [InlineData(Readme, -5, -1, CitationProblem.InvalidRange)]
    [InlineData(Readme, 5, 4, CitationProblem.InvalidRange)]
    [InlineData(Readme, 1, 100_000, CitationProblem.RangeTooLarge)]
    public async Task Fabricated_citation_is_rejected(string path, int startLine, int endLine, CitationProblem expected)
    {
        var check = Assert.Single(await indexed.ValidateAsync(new Citation(path, startLine, endLine)));

        Assert.False(check.IsValid);
        Assert.Equal(expected, check.Problem);
        Assert.Null(check.ChunkId);
        Assert.Null(check.Excerpt);
    }

    [Fact]
    public async Task Each_citation_is_judged_on_its_own()
    {
        var checks = await indexed.ValidateAsync(
            new Citation(Readme, 1, 3),
            new Citation("src/Invented.cs", 1, 3),
            new Citation(Readme, 28, 28));

        Assert.Equal([true, false, true], checks.Select(check => check.IsValid));
    }

    [Fact]
    public async Task File_that_exists_only_in_another_snapshot_of_the_repository_is_rejected()
    {
        using var copy = SampleRepo.CopyToTemp();
        await File.WriteAllTextAsync(Path.Combine(copy.Path, "docs", "later.md"), "# Added later\n");
        var newer = await indexed.Database.IndexAsync(indexed.RepositoryId, SampleRepo.Source(copy.Path));
        var citation = new Citation("docs/later.md", 1, 1);

        await using var scope = indexed.Database.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<CitationValidator>();
        var againstPinned = await validator.ValidateAsync(indexed.RepositoryId, indexed.SnapshotId, [citation]);
        var againstNewer = await validator.ValidateAsync(indexed.RepositoryId, newer.SnapshotId, [citation]);

        Assert.Equal(CitationProblem.PathNotInSnapshot, againstPinned[0].Problem);
        Assert.True(againstNewer[0].IsValid);
    }

    [Fact]
    public async Task Snapshot_of_another_repository_cannot_be_read_through_any_tool()
    {
        await using var db = indexed.Database.CreateContext();
        var otherRepository = await TestGraph.AddRepositoryAsync(db, "other-repo");

        await using var scope = indexed.Database.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<SourceTools>();
        var validator = scope.ServiceProvider.GetRequiredService<CitationValidator>();

        // A real snapshot ID, presented with a repository it does not belong to.
        await AssertErrorAsync(SourceToolError.SnapshotNotFound,
            () => tools.SearchCodeAsync(otherRepository.Id, indexed.SnapshotId, "page size", 5));
        await AssertErrorAsync(SourceToolError.SnapshotNotFound,
            () => tools.ReadFileRangeAsync(otherRepository.Id, indexed.SnapshotId, Readme, 1, 3));
        await AssertErrorAsync(SourceToolError.SnapshotNotFound,
            () => validator.ValidateAsync(otherRepository.Id, indexed.SnapshotId, [new Citation(Readme, 1, 3)]));

        // And an ID that does not exist at all gets the identical answer.
        await AssertErrorAsync(SourceToolError.SnapshotNotFound,
            () => tools.ReadFileRangeAsync(indexed.RepositoryId, Guid.NewGuid(), Readme, 1, 3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unpublished_snapshot_cannot_be_read(bool failed)
    {
        await using var db = indexed.Database.CreateContext();
        var (snapshot, _) = await TestGraph.AddBuildingSnapshotAsync(
            db, await TestGraph.AddRepositoryAsync(db, $"unpublished-{failed}"));
        if (failed)
        {
            snapshot.Fail();
            await db.SaveChangesAsync();
        }

        await using var scope = indexed.Database.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<SourceTools>();
        var validator = scope.ServiceProvider.GetRequiredService<CitationValidator>();

        await AssertErrorAsync(SourceToolError.SnapshotNotReady,
            () => tools.SearchCodeAsync(snapshot.RepositoryId, snapshot.Id, "line", 5));
        await AssertErrorAsync(SourceToolError.SnapshotNotReady,
            () => tools.ReadFileRangeAsync(snapshot.RepositoryId, snapshot.Id, "src/App.cs", 1, 3));
        await AssertErrorAsync(SourceToolError.SnapshotNotReady,
            () => validator.ValidateAsync(snapshot.RepositoryId, snapshot.Id, [new Citation("src/App.cs", 1, 3)]));
    }

    [Fact]
    public async Task Too_many_citations_are_refused()
    {
        var citations = Enumerable.Repeat(new Citation(Readme, 1, 1), CitationValidator.MaxCitations + 1).ToArray();

        await AssertErrorAsync(SourceToolError.InvalidArgument, () => indexed.ValidateAsync(citations));
    }

    [Fact]
    public async Task Snapshot_indexed_with_another_embedding_model_is_not_searched()
    {
        await using var db = indexed.Database.CreateContext();
        var repository = await TestGraph.AddRepositoryAsync(db, "other-model");
        var result = await indexed.Database.IndexAsync(repository.Id, SampleRepo.Source(), new OtherModelProvider());

        await using var scope = indexed.Database.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<SourceTools>();

        await AssertErrorAsync(SourceToolError.EmbeddingModelMismatch,
            () => tools.SearchCodeAsync(repository.Id, result.SnapshotId, "page size", 5));

        // Reading exact lines does not involve embeddings and still works.
        Assert.Equal(3, (await tools.ReadFileRangeAsync(repository.Id, result.SnapshotId, Readme, 1, 3)).Lines.Count);
    }

    private static async Task AssertErrorAsync(SourceToolError expected, Func<Task> act)
    {
        var exception = await Assert.ThrowsAsync<SourceToolException>(act);
        Assert.Equal(expected, exception.Error);
    }

    private sealed class OtherModelProvider : IEmbeddingProvider
    {
        public string ModelId => "other-model-v1";

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(_ =>
            {
                var vector = new float[SourceChunk.EmbeddingDimensions];
                vector[0] = 1f;
                return vector;
            })]);
    }
}
