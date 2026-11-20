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
        Assert.Equal(6, await CountApplicationTablesAsync(db));
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

        Assert.Equal(6, await CountApplicationTablesAsync(db));
    }

    private static Task<int> CountApplicationTablesAsync(TraceDbContext db) => db.Database
        .SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
            """)
        .SingleAsync();
}
