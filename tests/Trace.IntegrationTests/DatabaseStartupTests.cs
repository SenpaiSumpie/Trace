using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Trace.Domain;
using Trace.Infrastructure.Persistence;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class DatabaseStartupTests(PostgresFixture postgres, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Startup_migrates_an_empty_database_and_seeds_the_demo_repository()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        using (factory.WithDatabase(database.ConnectionString, initializeOnStartup: true).CreateClient())
        {
        }

        await using var db = database.CreateContext();
        var repository = await db.Repositories.SingleAsync();
        Assert.Equal(DemoData.RepositoryOwner, repository.Owner);
        Assert.Equal(DemoData.RepositoryName, repository.Name);
        Assert.Equal(RepositoryVisibility.Public, repository.Visibility);
        Assert.Null(repository.GitHubRepoId);
    }

    [Fact]
    public async Task Startup_is_idempotent_across_restarts()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        for (var restart = 0; restart < 2; restart++)
        {
            using var client = factory.WithDatabase(database.ConnectionString, initializeOnStartup: true).CreateClient();
        }

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Repositories.CountAsync());
    }

    [Fact]
    public async Task Startup_leaves_the_database_alone_when_initialization_is_disabled()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        using (factory.WithDatabase(database.ConnectionString, initializeOnStartup: false).CreateClient())
        {
        }

        await using var db = database.CreateContext();
        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());
    }
}
