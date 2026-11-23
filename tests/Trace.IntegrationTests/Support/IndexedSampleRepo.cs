using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Trace.Application.Retrieval;

namespace Trace.IntegrationTests.Support;

/// <summary>
/// The fixture repository indexed once into its own database, shared by read-only tests.
/// </summary>
public sealed class IndexedSampleRepo(PostgresFixture postgres) : IAsyncLifetime
{
    public TestDatabase Database { get; private set; } = null!;

    public Guid RepositoryId { get; private set; }

    public Guid SnapshotId { get; private set; }

    public string CommitSha { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Database = await postgres.CreateDatabaseAsync();
        await using var db = Database.CreateContext();
        RepositoryId = (await TestGraph.AddRepositoryAsync(db)).Id;

        var result = await Database.IndexAsync(RepositoryId, SampleRepo.Source());
        SnapshotId = result.SnapshotId;
        CommitSha = result.CommitSha;
    }

    public Task DisposeAsync() => Database.DisposeAsync().AsTask();

    public async Task<CodeSearchResult> SearchAsync(string query, int limit = 5)
    {
        await using var scope = Database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SourceTools>()
            .SearchCodeAsync(RepositoryId, SnapshotId, query, limit);
    }

    public async Task<FileRange> ReadAsync(string path, int startLine, int endLine)
    {
        await using var scope = Database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SourceTools>()
            .ReadFileRangeAsync(RepositoryId, SnapshotId, path, startLine, endLine);
    }

    public async Task<IReadOnlyList<CitationCheck>> ValidateAsync(params Citation[] citations)
    {
        await using var scope = Database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CitationValidator>()
            .ValidateAsync(RepositoryId, SnapshotId, citations);
    }
}

public sealed record LabeledRange(string Path, int StartLine, int EndLine);

/// <param name="Expected">The labeled locations; absent for a question the repository cannot answer.</param>
/// <param name="Answerable">False for a question whose right result is "not enough evidence".</param>
public sealed record LabeledQuestion(
    string Id,
    string Split,
    string Question,
    IReadOnlyList<LabeledRange>? Expected = null,
    bool Answerable = true)
{
    public IReadOnlyList<LabeledRange> Locations => Expected ?? [];
}

public sealed record LabeledQuestionSet(string DatasetVersion, string FixtureRevision, IReadOnlyList<LabeledQuestion> Questions)
{
    /// <summary>Loads <c>tests/fixtures/sample-repo.questions.json</c>.</summary>
    public static LabeledQuestionSet Load()
    {
        var path = SampleRepo.Path + ".questions.json";
        return JsonSerializer.Deserialize<LabeledQuestionSet>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"'{path}' is empty.");
    }
}
