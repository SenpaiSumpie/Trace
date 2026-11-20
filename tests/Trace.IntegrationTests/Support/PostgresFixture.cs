using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Trace.Infrastructure;
using Trace.Infrastructure.Persistence;

namespace Trace.IntegrationTests.Support;

/// <summary>One disposable PostgreSQL + pgvector container shared by all database tests.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database so each test starts from a clean, isolated state.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(bool migrate = true)
    {
        var name = $"test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = name,
        }.ConnectionString;

        var database = new TestDatabase(connectionString);
        if (migrate)
        {
            await using var db = database.CreateContext();
            await db.Database.MigrateAsync();
        }

        return database;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public sealed class TestDatabase : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly AsyncServiceScope scope;

    public TestDatabase(string connectionString)
    {
        ConnectionString = connectionString;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{InfrastructureServiceCollectionExtensions.ConnectionStringName}"] = connectionString,
            })
            .Build();

        provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddTraceInfrastructure()
            .BuildServiceProvider();
        scope = provider.CreateAsyncScope();
    }

    public string ConnectionString { get; }

    /// <summary>A fresh context, configured exactly as the application configures it.</summary>
    public TraceDbContext CreateContext() => new(
        scope.ServiceProvider.GetRequiredService<DbContextOptions<TraceDbContext>>(),
        TimeProvider.System);

    public async ValueTask DisposeAsync()
    {
        await scope.DisposeAsync();
        await provider.DisposeAsync();
    }
}
