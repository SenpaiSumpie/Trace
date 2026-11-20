using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trace.Application.Retrieval;
using Trace.Infrastructure.Embeddings;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class RetrievalTests(IndexedSampleRepo indexed) : IClassFixture<IndexedSampleRepo>
{
    private static readonly LabeledQuestionSet Labels = LabeledQuestionSet.Load();

    public static TheoryData<string> DevQuestionIds =>
        [.. Labels.Questions.Where(question => question.Split == "dev").Select(question => question.Id)];

    [Fact]
    public void Labels_were_written_for_the_indexed_fixture_revision()
    {
        Assert.Equal(Labels.FixtureRevision, indexed.CommitSha);
        Assert.Contains(Labels.Questions, question => question.Split == "dev");
        Assert.Contains(Labels.Questions, question => question.Split == "test");
    }

    /// <summary>Regression gate: every development question finds its range in the top 5 of 17 chunks.</summary>
    [Theory]
    [MemberData(nameof(DevQuestionIds))]
    public async Task Dev_question_retrieves_its_expected_ranges_in_the_top_5(string questionId)
    {
        var question = Labels.Questions.Single(candidate => candidate.Id == questionId);

        var rank = await RankOfExpectedAsync(question);

        Assert.InRange(rank, 1, 5);
    }

    /// <summary>
    /// Baseline over the whole labeled set, held-out questions included. The floors are the
    /// measured baseline with the deterministic fake embeddings (recall@5 = 9/11, recall@10 =
    /// 10/11), so this guards against regressions; it is not a claim about retrieval quality.
    /// The two known misses need semantic matching that a bag-of-words fake cannot provide.
    /// </summary>
    [Fact]
    public async Task Labeled_set_meets_the_recorded_recall_baseline()
    {
        var ranks = new Dictionary<string, int>();
        foreach (var question in Labels.Questions)
        {
            ranks[question.Id] = await RankOfExpectedAsync(question);
        }

        var report = string.Join(", ", ranks.Select(entry => $"{entry.Key}={entry.Value}"));
        Assert.True(ranks.Values.Count(rank => rank is >= 1 and <= 5) >= 9, $"recall@5 fell below 9/11: {report}");
        Assert.True(ranks.Values.Count(rank => rank is >= 1 and <= 10) >= 10, $"recall@10 fell below 10/11: {report}");
    }
    [Fact]
    public async Task Results_are_ranked_bounded_and_pinned_to_the_snapshot_commit()
    {
        var result = await indexed.SearchAsync("page size", limit: 3);

        Assert.Equal(indexed.CommitSha, result.CommitSha);
        Assert.Equal(3, result.Hits.Count);
        Assert.Equal(result.Hits.OrderByDescending(hit => hit.Score).Select(hit => hit.ChunkId), result.Hits.Select(hit => hit.ChunkId));
        Assert.All(result.Hits, hit => Assert.True(hit.LexicalRank is not null || hit.VectorRank is not null));
        Assert.Contains(result.Hits, hit => hit.LexicalRank is not null && hit.VectorRank is not null);
    }

    [Fact]
    public async Task Excerpts_are_the_exact_cited_lines()
    {
        var result = await indexed.SearchAsync("api key middleware");

        Assert.All(result.Hits, hit =>
        {
            var lines = SampleRepo.ReadLines(hit.Path);
            Assert.Equal(string.Join('\n', lines[(hit.StartLine - 1)..hit.EndLine]), hit.Excerpt);
        });
    }

    [Fact]
    public async Task Search_is_deterministic()
    {
        var first = await indexed.SearchAsync("how are reports generated", limit: 10);
        var second = await indexed.SearchAsync("how are reports generated", limit: 10);

        Assert.Equal(first.Hits, second.Hits);
    }

    [Theory]
    [InlineData("MaxPageSize")]
    [InlineData("max page size")]
    [InlineData("max_page_size")]
    public async Task Lexical_search_matches_identifiers_however_they_are_written(string query)
    {
        var hits = await LexicalAsync(query);

        Assert.Contains(hits, hit => hit.Path == "src/TaskBoard.Api/Tasks/PageRequest.cs");
        Assert.Contains(hits, hit => hit.Path == "src/TaskBoard.Api/appsettings.json");
    }

    [Fact]
    public async Task Lexical_search_matches_words_inside_dotted_member_access_and_paths()
    {
        // "request.PageSize" and the file path must be searchable as separate words.
        Assert.Contains(await LexicalAsync("request"), hit => hit.Path == "src/TaskBoard.Api/Tasks/TaskRepository.cs");
        Assert.Equal("src/TaskBoard.Api/Auth/ApiKeyMiddleware.cs", (await LexicalAsync("middleware"))[0].Path);
    }

    [Fact]
    public async Task Lexical_search_uses_stemming()
    {
        // The fixture says "rejects"; the query says "rejecting".
        Assert.Contains(await LexicalAsync("rejecting"), hit => hit.Text.Contains("rejects", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("the of and is")]
    [InlineData("zzzunknownword")]
    [InlineData("!!! ??? ...")]
    public async Task Lexical_search_returns_nothing_when_no_word_matches(string query)
    {
        Assert.Empty(await LexicalAsync(query));
    }

    [Theory]
    [InlineData("'; DROP TABLE source_chunks; --")]
    [InlineData("page & | ! ( ) :* <-> size")]
    [InlineData("\\ \" ' % _ \0 page")]
    public async Task Query_text_is_data_not_query_syntax(string query)
    {
        var result = await indexed.SearchAsync(query);

        Assert.NotNull(result.Hits);
        await using var db = indexed.Database.CreateContext();
        Assert.True(await db.SourceChunks.AnyAsync());
    }

    [Fact]
    public async Task Vector_search_ranks_a_chunks_own_text_first()
    {
        await using var scope = indexed.Database.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISnapshotReader>();
        var target = (await reader.GetChunksInRangeAsync(indexed.SnapshotId, "src/TaskBoard.Api/Reports/ReportQueue.cs", 1, 1))[0];
        var embedding = (await new DeterministicFakeEmbeddingProvider().EmbedAsync([target.Text]))[0];

        var nearest = await reader.SearchVectorAsync(indexed.SnapshotId, embedding, 3);

        Assert.Equal(target.Id, nearest[0].Id);
        Assert.Equal(3, nearest.Count);
    }

    [Fact]
    public async Task Search_only_sees_the_requested_snapshot()
    {
        using var copy = SampleRepo.CopyToTemp();
        await File.WriteAllTextAsync(Path.Combine(copy.Path, "docs", "zebracorn.md"), "# Zebracorn\n\nOnly in the newer revision.\n");
        var newer = await indexed.Database.IndexAsync(indexed.RepositoryId, SampleRepo.Source(copy.Path));

        await using var scope = indexed.Database.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<SourceTools>();
        var inOlder = await tools.SearchCodeAsync(indexed.RepositoryId, indexed.SnapshotId, "zebracorn", 20);
        var inNewer = await tools.SearchCodeAsync(indexed.RepositoryId, newer.SnapshotId, "zebracorn", 20);

        Assert.DoesNotContain(inOlder.Hits, hit => hit.Path == "docs/zebracorn.md");
        Assert.All(inOlder.Hits, hit => Assert.Null(hit.LexicalRank));
        Assert.Equal("docs/zebracorn.md", inNewer.Hits[0].Path);
        Assert.NotEqual(inOlder.CommitSha, inNewer.CommitSha);
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("   ", 5)]
    [InlineData("page", 0)]
    [InlineData("page", SourceTools.MaxSearchLimit + 1)]
    public async Task Search_rejects_out_of_bounds_arguments(string query, int limit)
    {
        var exception = await Assert.ThrowsAsync<SourceToolException>(() => indexed.SearchAsync(query, limit));

        Assert.Equal(SourceToolError.InvalidArgument, exception.Error);
    }

    [Fact]
    public async Task Search_rejects_an_overlong_query()
    {
        var exception = await Assert.ThrowsAsync<SourceToolException>(
            () => indexed.SearchAsync(new string('a', SourceTools.MaxQueryLength + 1)));

        Assert.Equal(SourceToolError.InvalidArgument, exception.Error);
    }

    [Fact]
    public async Task Read_file_range_returns_numbered_lines_across_chunk_boundaries()
    {
        var range = await indexed.ReadAsync("web/src/TaskList.tsx", 38, 45);

        var lines = SampleRepo.ReadLines("web/src/TaskList.tsx");
        Assert.Equal(indexed.CommitSha, range.CommitSha);
        Assert.Equal((38, 45), (range.StartLine, range.EndLine));
        Assert.Equal(Enumerable.Range(38, 8), range.Lines.Select(line => line.Number));
        Assert.Equal(lines[37..45], range.Lines.Select(line => line.Text));
    }

    [Fact]
    public async Task Read_file_range_clamps_the_end_to_the_last_line()
    {
        var range = await indexed.ReadAsync("src/TaskBoard.Api/Tasks/TaskItem.cs", 4, 150);

        var lines = SampleRepo.ReadLines("src/TaskBoard.Api/Tasks/TaskItem.cs");
        Assert.Equal((4, lines.Length), (range.StartLine, range.EndLine));
        Assert.Equal(lines[3..], range.Lines.Select(line => line.Text));
    }

    [Theory]
    [InlineData("src/DoesNotExist.cs", 1, 5, SourceToolError.PathNotFound)]
    [InlineData("../../../etc/passwd", 1, 5, SourceToolError.PathNotFound)]
    [InlineData("readme.md", 1, 5, SourceToolError.PathNotFound)]
    [InlineData("README.md\0", 1, 5, SourceToolError.PathNotFound)]
    [InlineData("README.md", 500, 510, SourceToolError.RangeOutOfBounds)]
    [InlineData("README.md", 0, 5, SourceToolError.InvalidArgument)]
    [InlineData("README.md", 5, 4, SourceToolError.InvalidArgument)]
    [InlineData("README.md", 1, SourceTools.MaxRangeLines + 1, SourceToolError.InvalidArgument)]
    [InlineData("", 1, 5, SourceToolError.InvalidArgument)]
    public async Task Read_file_range_rejects_unknown_paths_and_bad_ranges(
        string path, int startLine, int endLine, SourceToolError expected)
    {
        var exception = await Assert.ThrowsAsync<SourceToolException>(() => indexed.ReadAsync(path, startLine, endLine));

        Assert.Equal(expected, exception.Error);
    }

    /// <summary>1-based rank of the first hit overlapping every expected range, or 0 if not in the top 20.</summary>
    private async Task<int> RankOfExpectedAsync(LabeledQuestion question)
    {
        var result = await indexed.SearchAsync(question.Question, SourceTools.MaxSearchLimit);

        return question.Expected.Max(expected =>
        {
            var index = result.Hits.ToList().FindIndex(hit => hit.Path == expected.Path
                && hit.StartLine <= expected.EndLine
                && hit.EndLine >= expected.StartLine);
            return index < 0 ? int.MaxValue : index + 1;
        }) is var worst and < int.MaxValue ? worst : 0;
    }

    private async Task<IReadOnlyList<ChunkRecord>> LexicalAsync(string query)
    {
        await using var scope = indexed.Database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISnapshotReader>()
            .SearchLexicalAsync(indexed.SnapshotId, query, 20);
    }
}
