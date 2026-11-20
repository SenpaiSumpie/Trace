using Microsoft.EntityFrameworkCore;
using Trace.Domain;

namespace Trace.Infrastructure.Persistence;

/// <summary>The public synthetic repository used for local development and demos.</summary>
public static class DemoData
{
    public const string RepositoryOwner = "trace-demo";
    public const string RepositoryName = "sample-repo";

    /// <summary>Adds the demo repository if it is not already present.</summary>
    public static async Task SeedAsync(TraceDbContext db, CancellationToken cancellationToken = default)
    {
        var exists = await db.Repositories.AnyAsync(
            r => r.Owner == RepositoryOwner && r.Name == RepositoryName, cancellationToken);
        if (exists)
        {
            return;
        }

        db.Repositories.Add(Repository.Create(
            RepositoryOwner, RepositoryName, defaultBranch: "main", RepositoryVisibility.Public));
        await db.SaveChangesAsync(cancellationToken);
    }
}
