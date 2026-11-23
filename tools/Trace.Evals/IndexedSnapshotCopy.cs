using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Trace.Domain;
using Trace.Infrastructure;
using Trace.Infrastructure.Persistence;

namespace Trace.Evals;

/// <param name="SnapshotId">The same ID the snapshot has in the database it was copied from.</param>
public sealed record CopiedSnapshot(Guid SnapshotId, string CommitSha, string EmbeddingModel, int ChunkCount);

/// <summary>
/// Copies one completed snapshot, with its passages and their stored embeddings, from the
/// configured database into a run's throwaway database, so that a repository indexed through
/// the application can be evaluated without indexing or embedding it again (ADR-014).
/// <para>
/// The configured database is only read, on connections whose transactions PostgreSQL itself
/// keeps read-only. Nothing else is copied: not the repository row (its name stays behind),
/// not its installation or access rows, not any investigation.
/// </para>
/// <para>
/// This is not a way around the "may this caller see this repository" check. It needs the
/// database's own credentials, lives in a tool that no host may reference, and the pipeline
/// that then answers the questions makes its usual check against the copy.
/// </para>
/// </summary>
public static class IndexedSnapshotCopy
{
    /// <summary>Passages read and written per round trip; each carries a 1,024-number embedding.</summary>
    public const int BatchSize = 200;

    /// <summary>
    /// The same connection with every transaction read-only by default, so a statement that
    /// would change the database is refused by the server (SQLSTATE 25006).
    /// </summary>
    public static string ReadOnly(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.Options = string.IsNullOrWhiteSpace(builder.Options)
            ? "-c default_transaction_read_only=on"
            : builder.Options + " -c default_transaction_read_only=on";
        return builder.ConnectionString;
    }

    /// <param name="sourceConnectionString">The database that holds the snapshot. Only read.</param>
    /// <param name="target">The run's own database, already migrated.</param>
    /// <param name="targetRepositoryId">The repository in <paramref name="target"/> the copy will belong to.</param>
    /// <exception cref="EvalException">The source has no completed snapshot with that ID.</exception>
    public static async Task<CopiedSnapshot> CopyAsync(
        string sourceConnectionString,
        Guid snapshotId,
        TraceDbContext target,
        Guid targetRepositoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // A second composition of the same registration, pointed at the source: the tool reads
        // through the application's own model rather than through SQL of its own.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{InfrastructureServiceCollectionExtensions.ConnectionStringName}"] = ReadOnly(sourceConnectionString),
            })
            .Build();
        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddTraceInfrastructure()
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<TraceDbContext>();

        var snapshot = await source.IndexSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == snapshotId, cancellationToken);
        if (snapshot is not { Status: SnapshotStatus.Completed })
        {
            throw new EvalException(
                $"The configured database has no completed snapshot with the ID {snapshotId}. "
                + "List the snapshots as docs/evaluation.md describes and pass the ID of a completed one.");
        }

        // The schema lets passages be written only while their snapshot is being built, so the
        // copy is made the way an index is: a building snapshot, its passages, then completed.
        var completedAt = snapshot.CompletedAt;
        target.IndexSnapshots.Add(snapshot);
        var entry = target.Entry(snapshot);
        entry.Property(copied => copied.RepositoryId).CurrentValue = targetRepositoryId;
        entry.Property(copied => copied.BuildingJobId).CurrentValue = null;
        entry.Property(copied => copied.Status).CurrentValue = SnapshotStatus.Building;
        entry.Property(copied => copied.CompletedAt).CurrentValue = null;
        await target.SaveChangesAsync(cancellationToken);
        target.ChangeTracker.Clear();

        var copiedChunks = 0;
        var after = Guid.Empty;
        while (true)
        {
            var position = after;
            var chunks = await source.SourceChunks.AsNoTracking()
                .Where(chunk => chunk.SnapshotId == snapshotId && chunk.Id > position)
                .OrderBy(chunk => chunk.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (chunks.Count == 0)
            {
                break;
            }

            target.SourceChunks.AddRange(chunks);
            await target.SaveChangesAsync(cancellationToken);
            target.ChangeTracker.Clear();
            copiedChunks += chunks.Count;
            after = chunks[^1].Id;
        }

        entry = target.Attach(snapshot);
        entry.Property(copied => copied.Status).CurrentValue = SnapshotStatus.Completed;
        entry.Property(copied => copied.CompletedAt).CurrentValue = completedAt;
        await target.SaveChangesAsync(cancellationToken);
        target.ChangeTracker.Clear();

        return new CopiedSnapshot(snapshot.Id, snapshot.CommitSha, snapshot.EmbeddingModel, copiedChunks);
    }
}
