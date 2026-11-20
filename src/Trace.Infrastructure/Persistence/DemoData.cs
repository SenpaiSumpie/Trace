using Microsoft.EntityFrameworkCore;
using Trace.Application.Indexing;
using Trace.Domain;
using Trace.Infrastructure.Indexing;

namespace Trace.Infrastructure.Persistence;

/// <summary>The public synthetic repository used for local development and demos.</summary>
public static class DemoData
{
    public const string RepositoryOwner = "trace-demo";
    public const string RepositoryName = "sample-repo";
    public const string Branch = "main";

    /// <summary>
    /// Adds the demo repository if it is not already present and, when a fixture directory is
    /// given, indexes it. Safe to repeat: unchanged fixture content is not indexed again.
    /// </summary>
    public static async Task SeedAsync(
        TraceDbContext db,
        SnapshotIndexer indexer,
        string? fixtureDirectory,
        CancellationToken cancellationToken = default)
    {
        var repository = await db.Repositories.SingleOrDefaultAsync(
            r => r.Owner == RepositoryOwner && r.Name == RepositoryName, cancellationToken);
        if (repository is null)
        {
            repository = Repository.Create(RepositoryOwner, RepositoryName, Branch, RepositoryVisibility.Public);
            db.Repositories.Add(repository);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (fixtureDirectory is not null)
        {
            await indexer.IndexAsync(
                repository.Id, new LocalDirectorySource(fixtureDirectory, Branch), cancellationToken);
        }
    }
}
