using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Trace.Domain;
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
        Assert.Equal(12, await CountApplicationTablesAsync(db));
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

        Assert.Equal(12, await CountApplicationTablesAsync(db));
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

    [Fact]
    public async Task Plans_migration_upgrades_a_database_that_already_holds_investigations()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("InvestigationCacheUsage");

        // Rows as a database from before this migration holds them, written with raw SQL against
        // that schema: a finished question with evidence, and an investigation of the
        // issue_to_plan type, which the type column has allowed since the first migration.
        var repositoryId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var planInvestigationId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
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

            INSERT INTO investigations
                (id, repository_id, snapshot_id, type, question, state, result_json, created_at, updated_at)
            VALUES
                ({questionId}, {repositoryId}, {snapshotId}, 'question', 'Where is paging handled?', 'completed',
                 {"""{"schemaVersion":1}"""}::jsonb, now(), now()),
                ({planInvestigationId}, {repositoryId}, {snapshotId}, 'issue_to_plan', 'Plan issue 42', 'queued',
                 NULL, now(), now());

            INSERT INTO evidence_refs
                (id, investigation_id, snapshot_id, chunk_id, source_kind, locator_json, excerpt_hash, created_at, updated_at)
            VALUES
                ({evidenceId}, {questionId}, {snapshotId}, {chunkId}, 'code', {"{}"}::jsonb, repeat('0', 64), now(), now());
            """);

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(12, await CountApplicationTablesAsync(db));

        // Nothing that was there changed, and the current model reads it.
        var question = await db.Investigations.SingleAsync(investigation => investigation.Id == questionId);
        var planInvestigation = await db.Investigations.SingleAsync(investigation => investigation.Id == planInvestigationId);
        Assert.Equal(InvestigationType.Question, question.Type);
        Assert.Equal(InvestigationState.Completed, question.State);
        Assert.Contains("schemaVersion", question.ResultJson);
        Assert.Equal(InvestigationType.IssueToPlan, planInvestigation.Type);
        Assert.Equal(2, await db.Investigations.CountAsync());
        Assert.Equal(evidenceId, (await db.EvidenceRefs.SingleAsync()).Id);
        Assert.Equal(0, await db.Plans.CountAsync());

        // The new rules do not get in the way of what existed: a question's evidence can still be
        // removed, and an investigation from before the migration can be given a plan.
        db.Plans.Add(Plan.Create(planInvestigation, issueNumber: 42));
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Database.ExecuteSqlAsync($"DELETE FROM evidence_refs WHERE id = {evidenceId}"));

        // Stepping back removes only what this migration added, even with a plan stored.
        await migrator.MigrateAsync("InvestigationCacheUsage");

        Assert.Equal(11, await CountApplicationTablesAsync(db));
        Assert.Equal(0, await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_proc WHERE proname LIKE 'trace\\_%plan%'")
            .SingleAsync());
        Assert.Equal(2, await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM investigations")
            .SingleAsync());

        await migrator.MigrateAsync();
        Assert.Equal(12, await CountApplicationTablesAsync(db));
    }

    private static Task<int> CountApplicationTablesAsync(TraceDbContext db) => db.Database
        .SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
            """)
        .SingleAsync();
}
