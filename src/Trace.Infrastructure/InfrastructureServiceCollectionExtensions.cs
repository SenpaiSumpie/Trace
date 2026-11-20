using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Trace.Application.Access;
using Trace.Application.Indexing;
using Trace.Application.Investigations;
using Trace.Application.Jobs;
using Trace.Application.Retrieval;
using Trace.Infrastructure.Chat;
using Trace.Infrastructure.Embeddings;
using Trace.Infrastructure.GitHub;
using Trace.Infrastructure.Jobs;
using Trace.Infrastructure.Persistence;

namespace Trace.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string ConnectionStringName = "Trace";
    public const string ApplyMigrationsOnStartupKey = "Database:ApplyMigrationsOnStartup";
    public const string SeedDemoDataKey = "Database:SeedDemoData";

    /// <summary>Directory of the demo fixture repository; relative paths resolve against the content root.</summary>
    public const string DemoFixturePathKey = "Demo:FixturePath";

    public const string EmbeddingProviderKey = "Embeddings:Provider";
    public const string ChatProviderKey = "Chat:Provider";

    public static IServiceCollection AddTraceInfrastructure(this IServiceCollection services)
    {
        services.AddLogging();
        services.AddHttpClient();
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<TraceDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not configured.");
            TraceDbContext.Configure(options, connectionString);
        });

        services.AddSingleton(CreateEmbeddingProvider);
        services.AddScoped<ISnapshotStore, EfSnapshotStore>();
        services.AddScoped<SnapshotIndexer>();
        services.AddScoped<ISnapshotReader, EfSnapshotReader>();
        services.AddScoped<SourceTools>();
        services.AddScoped<CitationValidator>();

        services.AddSingleton(CreateChatModel);
        services.TryAddSingleton(CreateChatLimits);
        services.AddScoped<IRepositoryCatalog, EfRepositoryCatalog>();
        services.AddScoped<IInvestigationStore, EfInvestigationStore>();
        services.AddScoped<QuestionInvestigator>();

        services.TryAddScoped<ICurrentUser, AnonymousUser>();
        services.AddSingleton(CreateGitHubOptions);
        services.AddScoped(provider => new GitHubClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubClient.HttpClientName),
            provider.GetRequiredService<GitHubOptions>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<GitHubSignIn>();
        services.AddScoped<GitHubWebhooks>();

        services.TryAddSingleton(new JobOptions());
        services.AddSingleton<IJobSignal, JobSignal>();
        services.AddScoped<IJobQueue, EfJobQueue>();
        services.AddSingleton<IJobLeaseKeeper, ScopedJobLeaseKeeper>();
        services.AddScoped<IRepositorySourceResolver, ConfiguredRepositorySourceResolver>();
        services.AddScoped<IJobHandler, InvestigationJobHandler>();
        services.AddScoped<IJobHandler, IndexJobHandler>();
        services.AddScoped<JobProcessor>();
        services.AddScoped<JobService>();

        return services;
    }

    /// <summary>
    /// Makes this host process jobs from the queue. The worker always does; the API does only
    /// when <c>Jobs:ProcessInThisHost</c> is true, which is how a single process serves local
    /// development and browser tests.
    /// </summary>
    public static IServiceCollection AddTraceJobProcessing(this IServiceCollection services) =>
        services.AddHostedService<JobWorkerService>();

    public const string ProcessJobsInThisHostKey = "Jobs:ProcessInThisHost";

    public static bool ShouldProcessJobsInThisHost(IConfiguration configuration) =>
        IsEnabled(configuration, ProcessJobsInThisHostKey);

    /// <summary>
    /// Applies migrations and/or seeds demo data when the corresponding settings are enabled.
    /// Demo data is refused outright in the Production environment.
    /// </summary>
    public static async Task InitializeTraceDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var environment = services.GetRequiredService<IHostEnvironment>();
        var applyMigrations = IsEnabled(configuration, ApplyMigrationsOnStartupKey);
        var seedDemoData = IsEnabled(configuration, SeedDemoDataKey);

        if (seedDemoData && environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"'{SeedDemoDataKey}' must not be enabled in the Production environment.");
        }

        if (!applyMigrations && !seedDemoData)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceDbContext>();

        if (applyMigrations)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        if (seedDemoData)
        {
            var fixtureDirectory = configuration[DemoFixturePathKey] is { Length: > 0 } path
                ? Path.GetFullPath(path, environment.ContentRootPath)
                : null;
            await DemoData.SeedAsync(
                db, scope.ServiceProvider.GetRequiredService<SnapshotIndexer>(), fixtureDirectory, cancellationToken);
        }
    }

    /// <summary>GitHub App settings: GitHub:ClientId, ClientSecret, PrivateKey (PEM) and WebhookSecret.</summary>
    private static GitHubOptions CreateGitHubOptions(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var options = new GitHubOptions
        {
            ClientId = configuration["GitHub:ClientId"],
            ClientSecret = configuration["GitHub:ClientSecret"],
            // Allow the PEM to be supplied on one line with literal \n, as most secret stores require.
            PrivateKey = configuration["GitHub:PrivateKey"]?.Replace("\\n", "\n", StringComparison.Ordinal),
            WebhookSecret = configuration["GitHub:WebhookSecret"],
        };
        if (configuration["GitHub:ApiBaseUrl"] is { Length: > 0 } api)
        {
            options.ApiBaseUrl = new Uri(api);
        }

        if (configuration["GitHub:WebBaseUrl"] is { Length: > 0 } web)
        {
            options.WebBaseUrl = new Uri(web);
        }

        return options;
    }

    private static IEmbeddingProvider CreateEmbeddingProvider(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        switch (RequireProvider(configuration, EmbeddingProviderKey))
        {
            case DeterministicFakeEmbeddingProvider.ProviderName:
                RefuseFakeInProduction(services, EmbeddingProviderKey);
                return new DeterministicFakeEmbeddingProvider();

            case VoyageEmbeddingProvider.ProviderName:
                var options = new VoyageOptions();
                options.Model = configuration["Embeddings:Model"] is { Length: > 0 } model ? model : options.Model;
                return new VoyageEmbeddingProvider(
                    services.GetRequiredService<IHttpClientFactory>().CreateClient(VoyageEmbeddingProvider.ProviderName),
                    RequireSecret(configuration, "Embeddings:ApiKey", "VOYAGE_API_KEY"),
                    options,
                    services.GetRequiredService<TimeProvider>());

            case var unknown:
                throw new InvalidOperationException($"'{EmbeddingProviderKey}' has unknown value '{unknown}'.");
        }
    }

    private static IChatModel CreateChatModel(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        switch (RequireProvider(configuration, ChatProviderKey))
        {
            case DeterministicFakeChatModel.ProviderName:
                RefuseFakeInProduction(services, ChatProviderKey);
                return new DeterministicFakeChatModel();

            case AnthropicChatModel.ProviderName:
                var options = new AnthropicChatOptions();
                options.Model = configuration["Chat:Model"] is { Length: > 0 } model ? model : options.Model;
                if (configuration["Chat:Effort"] is { Length: > 0 } effort)
                {
                    options.Effort = Enum.TryParse<Anthropic.Models.Messages.Effort>(effort, ignoreCase: true, out var parsed)
                        ? parsed
                        : throw new InvalidOperationException($"'Chat:Effort' has unknown value '{effort}'.");
                }

                return new AnthropicChatModel(
                    new Anthropic.AnthropicClient { ApiKey = RequireSecret(configuration, "Chat:ApiKey", "ANTHROPIC_API_KEY") },
                    options);

            case var unknown:
                throw new InvalidOperationException($"'{ChatProviderKey}' has unknown value '{unknown}'.");
        }
    }

    /// <summary>There is no default provider: a deployment without one fails loudly.</summary>
    private static string RequireProvider(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } provider
            ? provider
            : throw new InvalidOperationException($"'{key}' is not configured.");

    private static void RefuseFakeInProduction(IServiceProvider services, string key)
    {
        if (services.GetRequiredService<IHostEnvironment>().IsProduction())
        {
            throw new InvalidOperationException(
                $"'{key}' must not be a deterministic fake in the Production environment.");
        }
    }

    /// <summary>
    /// Reads an API key from configuration (a secret store or user secrets) or the provider's
    /// usual environment variable. The value is never logged or stored by TRACE.
    /// </summary>
    private static string RequireSecret(IConfiguration configuration, string key, string environmentVariable) =>
        configuration[key] is { Length: > 0 } configured
            ? configured
            : configuration[environmentVariable] is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : throw new InvalidOperationException(
                    $"An API key is required: set '{key}' or the {environmentVariable} environment variable.");

    public const string MaxTokensPerInvestigationKey = "Chat:MaxTokensPerInvestigation";
    public const string DailyTokenBudgetKey = "Chat:DailyTokenBudget";

    private static ChatLimits CreateChatLimits(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var limits = new ChatLimits();
        limits.MaxTokensPerInvestigation = PositiveLong(configuration, MaxTokensPerInvestigationKey) ?? limits.MaxTokensPerInvestigation;
        limits.DailyTokenBudget = PositiveLong(configuration, DailyTokenBudgetKey) ?? limits.DailyTokenBudget;
        return limits;
    }

    private static long? PositiveLong(IConfiguration configuration, string key) =>
        configuration[key] is not { Length: > 0 } value
            ? null
            : long.TryParse(value, out var parsed) && parsed > 0
                ? parsed
                : throw new InvalidOperationException($"'{key}' must be a positive whole number but was '{value}'.");

    private static bool IsEnabled(IConfiguration configuration, string key) =>
        configuration[key] is { } value
        && (bool.TryParse(value, out var enabled)
            ? enabled
            : throw new InvalidOperationException($"'{key}' must be 'true' or 'false' but was '{value}'."));
}
