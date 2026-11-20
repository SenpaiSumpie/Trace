using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Npgsql;
using Testcontainers.PostgreSql;
using Trace.Application.Indexing;
using Trace.Infrastructure;
using Trace.Infrastructure.Embeddings;
using Trace.Infrastructure.Persistence;

namespace Trace.IntegrationTests.Support;

/// <summary>One disposable PostgreSQL + pgvector container shared by all database tests.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Every test gets its own database, and each database keeps its own idle connection pool,
    // so the suite as a whole needs far more than PostgreSQL's default of 100 connections.
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithCommand("-c", "max_connections=1000")
        .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database so each test starts from a clean, isolated state.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(
        bool migrate = true,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? settings = null)
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

        var database = new TestDatabase(connectionString, configureServices, settings);
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

    public TestDatabase(
        string connectionString,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        ConnectionString = connectionString;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{InfrastructureServiceCollectionExtensions.ConnectionStringName}"] = connectionString,
                [InfrastructureServiceCollectionExtensions.EmbeddingProviderKey] = DeterministicFakeEmbeddingProvider.ProviderName,
                [InfrastructureServiceCollectionExtensions.ChatProviderKey] = "deterministic-fake",
            })
            .Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IHostEnvironment>(new HostingEnvironment
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            })
            .AddTraceInfrastructure();

        // Registered last so a test can replace any application service (model, clock, options).
        configureServices?.Invoke(services);
        provider = services.BuildServiceProvider();
        scope = provider.CreateAsyncScope();
    }

    public string ConnectionString { get; }

    /// <summary>A fresh context, configured exactly as the application configures it.</summary>
    public TraceDbContext CreateContext() => new(
        scope.ServiceProvider.GetRequiredService<DbContextOptions<TraceDbContext>>(),
        TimeProvider.System);

    /// <summary>
    /// Runs one index operation in its own scope, as a request or job would. The application's
    /// configured embedding provider is used unless one is supplied.
    /// </summary>
    public async Task<IndexResult> IndexAsync(
        Guid repositoryId,
        IRepositorySource source,
        IEmbeddingProvider? embeddings = null)
    {
        await using var operation = provider.CreateAsyncScope();
        var indexer = embeddings is null
            ? operation.ServiceProvider.GetRequiredService<SnapshotIndexer>()
            : new SnapshotIndexer(
                operation.ServiceProvider.GetRequiredService<ISnapshotStore>(), embeddings, TimeProvider.System);

        return await indexer.IndexAsync(repositoryId, source);
    }

    /// <summary>A scope for resolving application services, as one request or job would get.</summary>
    public AsyncServiceScope CreateScope() => provider.CreateAsyncScope();

    public async ValueTask DisposeAsync()
    {
        await scope.DisposeAsync();
        await provider.DisposeAsync();
    }
}
