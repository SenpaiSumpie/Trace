using Trace.Domain;
using Trace.Infrastructure.Persistence;

namespace Trace.IntegrationTests.Support;

/// <summary>Builds valid rows through the domain model, the way application code will.</summary>
internal static class TestGraph
{
    public const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static async Task<Repository> AddRepositoryAsync(TraceDbContext db, string name = "sample-repo")
    {
        var repository = Repository.Create("trace-demo", name, "main", RepositoryVisibility.Public);
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        return repository;
    }

    public static IndexSnapshot NewSnapshot(Repository repository, string commitSha = ShaA) =>
        IndexSnapshot.Start(repository.Id, commitSha, "main", parserVersion: "text-v1", embeddingModel: "fake-v1");

    /// <summary>Adds a building snapshot with one chunk.</summary>
    public static async Task<(IndexSnapshot Snapshot, SourceChunk Chunk)> AddBuildingSnapshotAsync(
        TraceDbContext db,
        Repository repository,
        string commitSha = ShaA)
    {
        var snapshot = NewSnapshot(repository, commitSha);
        var chunk = SourceChunk.Create(snapshot, "src/App.cs", "csharp", 1, 3, "line one\nline two\nline three");
        db.AddRange(snapshot, chunk);
        await db.SaveChangesAsync();
        return (snapshot, chunk);
    }

    /// <summary>Adds a snapshot with one chunk and publishes it.</summary>
    public static async Task<(IndexSnapshot Snapshot, SourceChunk Chunk)> AddCompletedSnapshotAsync(
        TraceDbContext db,
        Repository repository,
        string commitSha = ShaA)
    {
        var (snapshot, chunk) = await AddBuildingSnapshotAsync(db, repository, commitSha);
        snapshot.Complete(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return (snapshot, chunk);
    }

    public static async Task<Investigation> AddInvestigationAsync(TraceDbContext db, IndexSnapshot snapshot)
    {
        var investigation = Investigation.Create(snapshot, InvestigationType.IssueToPlan, "Where is paging handled?");
        db.Investigations.Add(investigation);
        await db.SaveChangesAsync();
        return investigation;
    }
}
