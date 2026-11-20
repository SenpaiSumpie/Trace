using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Trace.Infrastructure.Persistence;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_apply_and_match_the_model()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(11, await CountApplicationTablesAsync(db));
    }

    [Fact]
    public async Task Migrations_roll_back_completely_and_reapply()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(Migration.InitialDatabase);

        Assert.Equal(0, await CountApplicationTablesAsync(db));
        Assert.Equal(0, await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_proc WHERE proname LIKE 'trace\\_%'")
            .SingleAsync());

        await migrator.MigrateAsync();

        Assert.Equal(11, await CountApplicationTablesAsync(db));
    }

    [Fact]
    public async Task Full_text_search_migration_backfills_chunks_of_already_completed_snapshots()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("ChunkEmbeddings");
        // Raw SQL against the schema as it was at that migration: the current model has moved on.
        var repositoryId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO repositories (id, owner, name, default_branch, visibility, created_at, updated_at)
            VALUES ({repositoryId}, 'trace-demo', 'sample-repo', 'main', 'public', now(), now());

            INSERT INTO index_snapshots
                (id, repository_id, commit_sha, branch, parser_version, embedding_model, status, created_at, updated_at)
            VALUES
                ({snapshotId}, {repositoryId}, {TestGraph.ShaA}, 'main', 'text-v1', 'fake-v1', 'building', now(), now());

            INSERT INTO source_chunks
                (id, snapshot_id, path, language, start_line, end_line, content_hash, text, embedding, created_at, updated_at)
            VALUES
                ({chunkId}, {snapshotId}, 'src/App.cs', 'csharp', 1, 1, repeat('0', 64), 'line one',
                 array_fill(0, ARRAY[1024])::vector, now(), now());

            UPDATE index_snapshots SET status = 'completed', completed_at = now() WHERE id = {snapshotId};
            """);

        await migrator.MigrateAsync();

        var searchable = await db.Database
            .SqlQuery<bool>($"SELECT search_vector @@ plainto_tsquery('english', 'line') AS \"Value\" FROM source_chunks WHERE id = {chunkId}")
            .SingleAsync();
        Assert.True(searchable);
    }

    private static Task<int> CountApplicationTablesAsync(TraceDbContext db) => db.Database
        .SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
            """)
        .SingleAsync();
}
