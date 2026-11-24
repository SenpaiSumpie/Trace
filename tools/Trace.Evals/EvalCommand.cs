using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trace.Application.Indexing;
using Trace.Application.Investigations;
using Trace.Domain;
using Trace.Infrastructure;
using Trace.Infrastructure.Indexing;
using Trace.Infrastructure.Persistence;

namespace Trace.Evals;

/// <summary>The command line is wrong; the message says how.</summary>
public sealed class EvalUsageException(string message) : Exception(message);

/// <param name="SourceDirectory">The repository as a local directory to index; null when <paramref name="SnapshotId"/> is given.</param>
/// <param name="SnapshotId">An already indexed snapshot in the configured database to evaluate; null when a directory is given.</param>
public sealed record EvalOptions(
    string DatasetPath,
    string? SourceDirectory,
    string? OutputDirectory,
    string? Split,
    bool AllowRevisionMismatch,
    bool KeepDatabase,
    string? ExpectationsPath = null,
    Guid? SnapshotId = null);

/// <summary>
/// The tool's entry point and its <c>run</c> command: put a snapshot into a throwaway database (by indexing a source
/// directory, or by copying one that is already indexed), run a dataset's questions against it through the production
/// pipeline, and write one result file.
/// </summary>
public static partial class EvalCommand
{
    public const int ExitOk = 0;
    public const int ExitError = 1;

    /// <summary>The run was stopped early; a result file was still written.</summary>
    public const int ExitCancelled = 2;

    /// <summary>The <c>check</c> command found an expectation that the result file does not meet.</summary>
    public const int ExitExpectationsNotMet = 3;

    /// <summary>Relative to the repository root; git-ignored.</summary>
    public const string DefaultOutputDirectory = "eval/results";

    /// <summary>
    /// Where results go when an already indexed snapshot is evaluated. Such a snapshot is usually
    /// of a private repository, and this directory is the one place for anything about those.
    /// Relative to the repository root; git-ignored.
    /// </summary>
    public const string DefaultSnapshotOutputDirectory = "eval/local/results";

    /// <summary>
    /// The tool is not a deployment: it serves no user and writes only to its own throwaway
    /// database. Its environment is therefore never Production, which is the only environment
    /// that refuses the deterministic fake providers.
    /// </summary>
    public const string EnvironmentName = "Evaluation";

    public const string SettingsFile = "evalsettings.json";
    public const string QuestionTimeoutKey = "Eval:QuestionTimeoutSeconds";
    public const string SourceBranch = "main";
    public const string UnknownCommit = "unknown";

    public const string Usage = """
        Usage:
          dotnet run --project tools/Trace.Evals -- run --dataset <file> --source <directory> [options]
          dotnet run --project tools/Trace.Evals -- run --dataset <file> --snapshot <snapshot id> [options]
          dotnet run --project tools/Trace.Evals -- report <result file> [--output <directory>]
          dotnet run --project tools/Trace.Evals -- compare <baseline result file> <candidate result file> [--output <directory>]
          dotnet run --project tools/Trace.Evals -- check <result file> --expectations <file>
          dotnet run --project tools/Trace.Evals -- plan-run --labels <file> --issues <directory> --source <directory> [options]
          dotnet run --project tools/Trace.Evals -- plan-report <plan result file> [--ratings <file>] [--output <directory>]

        run: asks every question of a dataset against one snapshot and writes one result file.
        Give either --source or --snapshot.

          --dataset <file>            Labeled questions (see docs/evaluation.md).
          --source <directory>        The repository to index, as a local directory.
          --snapshot <snapshot id>    A completed snapshot in the configured database, indexed
                                      earlier through the application. It is copied, never changed.
          --output <directory>        Where the result file goes. Default: eval/results, or
                                      eval/local/results with --snapshot (both git-ignored).
          --split dev|test            Run only one split. Default: all questions.
          --expectations <file>       Run only the questions an expectations file lists.
          --allow-revision-mismatch   Run even if the source is not the revision the dataset names.
          --keep-database             Do not drop the run's database afterwards.

        Providers, the database server, prices and the per-question timeout come from
        tools/Trace.Evals/evalsettings.json and environment variables.

        report: prints the metrics of one result file, overall and per split.
        compare: prints what changed between two result files, question by question, regressions first.

          --output <directory>        Where the Markdown and JSON files go. Default: next to the
                                      (candidate) result file.

        check: exits 3 when the result file does not meet the expectations file (see docs/evaluation.md).

        None of the three needs the database or a provider.

        plan-run: has the production pipeline plan every labeled issue against one snapshot and
        writes one plan result file (see docs/evaluation.md, "Plan evaluation").

          --labels <file>             Labeled issues.
          --issues <directory>        The issues, one JSON file each. Never read from GitHub.
          --source <directory>        The repository to index, as a local directory.
          --output <directory>        Where the result file goes. Default: eval/results.
          --split dev|test            Run only one split. Default: all labeled issues.
          --issue <number>            Run only this issue; may be repeated. For a probe or for
                                      the issues a stopped run did not reach.
          --allow-revision-mismatch   Run even if the source is not the revision the labels name.
          --keep-database             Do not drop the run's database afterwards.

        plan-report: scores a plan result file against the rubric, and writes the report, the
        rating sheet and an empty ratings file next to it. With --ratings it adds the part of the
        score a person rated. Needs no database and no provider.
        """;

    public static EvalOptions ParseArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args[0] != "run")
        {
            throw new EvalUsageException("Expected a command: 'run', 'report', 'compare', 'check', 'plan-run' or 'plan-report'.");
        }

        string? dataset = null;
        string? source = null;
        string? output = null;
        string? split = null;
        string? expectations = null;
        Guid? snapshot = null;
        var allowRevisionMismatch = false;
        var keepDatabase = false;

        for (var index = 1; index < args.Count; index++)
        {
            string Value() => index + 1 < args.Count
                ? args[++index]
                : throw new EvalUsageException($"'{args[index]}' needs a value.");

            switch (args[index])
            {
                case "--dataset":
                    dataset = Value();
                    break;
                case "--source":
                    source = Value();
                    break;
                case "--snapshot":
                    snapshot = Guid.TryParseExact(Value(), "D", out var snapshotId) && snapshotId != Guid.Empty
                        ? snapshotId
                        : throw new EvalUsageException("'--snapshot' must be a snapshot ID such as 01234567-89ab-7cde-8f01-23456789abcd.");
                    break;
                case "--output":
                    output = Value();
                    break;
                case "--split":
                    split = Value();
                    if (split is not (EvalDataset.DevSplit or EvalDataset.TestSplit))
                    {
                        throw new EvalUsageException($"'--split' must be '{EvalDataset.DevSplit}' or '{EvalDataset.TestSplit}'.");
                    }

                    break;
                case "--expectations":
                    expectations = Value();
                    break;
                case "--allow-revision-mismatch":
                    allowRevisionMismatch = true;
                    break;
                case "--keep-database":
                    keepDatabase = true;
                    break;
                default:
                    throw new EvalUsageException($"Unknown argument '{args[index]}'.");
            }
        }

        if (split is not null && expectations is not null)
        {
            throw new EvalUsageException("'--split' and '--expectations' cannot be combined: an expectations file already names its questions.");
        }

        if (source is not null && snapshot is not null)
        {
            throw new EvalUsageException("'--source' and '--snapshot' cannot be combined: a run evaluates one snapshot.");
        }

        if (dataset is null)
        {
            throw new EvalUsageException("'--dataset' is required.");
        }

        if (source is null && snapshot is null)
        {
            throw new EvalUsageException("'--source' or '--snapshot' is required.");
        }

        return new EvalOptions(dataset, source, output, split, allowRevisionMismatch, keepDatabase, expectations, snapshot);
    }

    /// <param name="configure">
    /// Runs after the tool's own configuration and service registration, so a test can point the
    /// run at another database server or replace the chat model.
    /// </param>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Action<HostApplicationBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (EvalReportCommand.Handles(args))
        {
            return await EvalReportCommand.RunAsync(args, output, error, cancellationToken);
        }

        if (EvalCheckCommand.Handles(args))
        {
            return await EvalCheckCommand.RunAsync(args, output, error);
        }

        if (PlanEvalCommand.Handles(args))
        {
            return await PlanEvalCommand.RunAsync(args, output, error, configure, cancellationToken);
        }

        EvalOptions options;
        EvalDataset dataset;
        IReadOnlySet<string>? questionIds;
        try
        {
            options = ParseArguments(args);
            dataset = EvalDataset.Load(options.DatasetPath);
            questionIds = options.ExpectationsPath is null ? null : EvalExpectations.Load(options.ExpectationsPath).SelectFrom(dataset);
            if (options.SourceDirectory is not null && !Directory.Exists(options.SourceDirectory))
            {
                throw new EvalUsageException($"The source directory '{options.SourceDirectory}' does not exist.");
            }
        }
        catch (EvalUsageException exception)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync();
            await error.WriteLineAsync(Usage);
            return ExitError;
        }
        catch (Exception exception) when (exception is EvalDatasetException or EvalExpectationsException)
        {
            await error.WriteLineAsync(exception.Message);
            return ExitError;
        }

        try
        {
            return await RunAsync(options, dataset, questionIds, output, error, configure, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"The evaluation run could not be completed: {exception.Message}");
            return ExitError;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Cancelled before any question was run; no result file was written.");
            return ExitCancelled;
        }
    }

    private static async Task<int> RunAsync(
        EvalOptions options,
        EvalDataset dataset,
        IReadOnlySet<string>? questionIds,
        TextWriter output,
        TextWriter error,
        Action<HostApplicationBuilder>? configure,
        CancellationToken cancellationToken)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = EnvironmentName,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(SettingsFile, optional: false)
            .AddEnvironmentVariables();
        builder.Logging
            .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(LogLevel.Warning)

            // Migrating an empty database logs a failed probe for the history table as an error.
            // A command that really fails still surfaces as an exception.
            .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
        builder.Services.AddTraceInfrastructure();
        builder.Services.AddSingleton<EvalRunner>();
        configure?.Invoke(builder);

        var configuration = builder.Configuration;
        var serverConnectionString = configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{InfrastructureServiceCollectionExtensions.ConnectionStringName}' is not configured.");
        var timeout = QuestionTimeout(configuration);

        var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
        var outputDirectory = Path.GetFullPath(options.OutputDirectory
            ?? Path.Combine(
                repositoryRoot ?? Directory.GetCurrentDirectory(),
                options.SnapshotId is null ? DefaultOutputDirectory : DefaultSnapshotOutputDirectory));
        var (traceCommit, traceCommitDirty) = ReadTraceCommit(repositoryRoot);

        await using var database = await ThrowawayDatabase.CreateAsync(
            serverConnectionString, options.KeepDatabase, cancellationToken);

        // From here on every service of the pipeline talks to the throwaway database only. With
        // --snapshot the configured database is still read once, by IndexedSnapshotCopy below.
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{InfrastructureServiceCollectionExtensions.ConnectionStringName}"] = database.ConnectionString,
        });

        using var host = builder.Build();
        var model = host.Services.GetRequiredService<IChatModel>();
        var prices = ModelPrices.FromConfiguration(configuration, model.ModelId);
        if (prices is null)
        {
            await error.WriteLineAsync(
                $"No prices are configured for model '{model.ModelId}' ({ModelPrices.Section}:{model.ModelId}); costs are recorded as null.");
        }

        Guid repositoryId;
        Guid snapshotId;
        string commitSha;
        int chunkCount;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TraceDbContext>();
            await db.Database.MigrateAsync(cancellationToken);

            // Public and not from GitHub: the one kind of repository an anonymous caller may see.
            // It is named after the dataset in both modes, never after the repository it came from.
            var repository = Repository.Create("trace-eval", dataset.Name, SourceBranch, RepositoryVisibility.Public);
            db.Repositories.Add(repository);
            await db.SaveChangesAsync(cancellationToken);
            repositoryId = repository.Id;

            if (options.SnapshotId is { } existing)
            {
                // The configured database is read here and nowhere else, and never written.
                var copied = await IndexedSnapshotCopy.CopyAsync(serverConnectionString, existing, db, repositoryId, cancellationToken);
                var embeddingModel = host.Services.GetRequiredService<IEmbeddingProvider>().ModelId;
                if (copied.EmbeddingModel != embeddingModel)
                {
                    // Otherwise every search of the run would fail with embedding_model_mismatch.
                    throw new EvalException(
                        $"The snapshot was indexed with the embedding model '{copied.EmbeddingModel}' but this run is configured for "
                        + $"'{embeddingModel}'. Set '{InfrastructureServiceCollectionExtensions.EmbeddingProviderKey}' to the provider "
                        + "the snapshot was indexed with.");
                }

                (snapshotId, commitSha, chunkCount) = (copied.SnapshotId, copied.CommitSha, copied.ChunkCount);
            }
            else
            {
                var index = await scope.ServiceProvider.GetRequiredService<SnapshotIndexer>().IndexAsync(
                    repositoryId, new LocalDirectorySource(options.SourceDirectory!, SourceBranch), cancellationToken);
                (snapshotId, commitSha, chunkCount) = (index.SnapshotId, index.CommitSha, index.ChunkCount);
            }
        }

        await output.WriteLineAsync(
            $"Dataset {dataset.Name} v{dataset.Version}; snapshot {commitSha[..12]} ({chunkCount} passages"
            + (options.SnapshotId is null ? string.Empty : ", copied from the configured database")
            + $"); model {model.ModelId}; database {database.Name}.");

        var request = new EvalRunRequest(
            dataset,
            repositoryId,
            snapshotId,
            configuration[InfrastructureServiceCollectionExtensions.ChatProviderKey]!,
            configuration[InfrastructureServiceCollectionExtensions.EmbeddingProviderKey]!,
            prices,
            traceCommit,
            traceCommitDirty,
            timeout,
            options.Split,
            options.AllowRevisionMismatch,
            questionIds);

        var number = 0;
        var result = await host.Services.GetRequiredService<EvalRunner>().RunAsync(
            request,
            // Identifiers, counts and times only: never the question or the answer.
            question => output.WriteLine(
                $"  {++number,3}. {question.Id}: {question.Outcome}"
                + (question.ErrorCode is null ? string.Empty : $" ({question.ErrorCode})")
                + $", {question.Evidence.Count} evidence, {question.ToolCalls.Count} tool call(s), {question.DurationMs} ms"),
            cancellationToken);

        var path = await result.WriteAsync(outputDirectory, CancellationToken.None);
        var totals = result.Totals;
        await output.WriteLineAsync(
            $"{totals.Questions} question(s): {totals.Answered} answered, {totals.InsufficientEvidence} insufficient evidence, "
            + $"{totals.Failed} failed, {totals.Cancelled} cancelled, {totals.NotRun} not run. "
            + $"Tokens: {totals.Tokens.Input} in, {totals.Tokens.Output} out. "
            + (totals.EstimatedCostUsd is { } cost
                ? $"Estimated cost: ${cost.ToString("0.0000", CultureInfo.InvariantCulture)}."
                : "Cost not estimated."));
        await output.WriteLineAsync($"Result file: {path}");
        if (options.KeepDatabase)
        {
            await output.WriteLineAsync($"Database {database.Name} was kept; drop it when you are done.");
        }

        return result.Status == EvalRunStatus.Completed ? ExitOk : ExitCancelled;
    }

    /// <summary>The longest one question, or one issue of a plan run, may take.</summary>
    internal static TimeSpan QuestionTimeout(IConfiguration configuration) =>
        int.TryParse(configuration[QuestionTimeoutKey], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidOperationException(
                $"'{QuestionTimeoutKey}' must be a positive whole number of seconds but was '{configuration[QuestionTimeoutKey]}'.");

    /// <summary>The nearest directory at or above the given one that holds <c>Trace.slnx</c>.</summary>
    public static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trace.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }

    /// <summary>The commit of TRACE being run and whether the working tree differs from it, asked from git.</summary>
    public static (string Commit, bool? Dirty) ReadTraceCommit(string? repositoryRoot)
    {
        if (repositoryRoot is null
            || Git(repositoryRoot, "rev-parse", "HEAD") is not { } head
            || !FullCommitSha().IsMatch(head.Trim()))
        {
            return (UnknownCommit, null);
        }

        var status = Git(repositoryRoot, "status", "--porcelain");
        return (head.Trim(), status is null ? null : status.Trim().Length > 0);
    }

    private static string? Git(string workingDirectory, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            // Read before waiting so a full pipe cannot block git.
            var errors = process.StandardError.ReadToEndAsync();
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            errors.Wait();
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            // git is not installed, or this is not a checkout.
            return null;
        }
    }

    [GeneratedRegex(@"^[0-9a-f]{40}\z")]
    private static partial Regex FullCommitSha();
}
