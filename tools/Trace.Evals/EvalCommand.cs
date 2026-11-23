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

public sealed record EvalOptions(
    string DatasetPath,
    string SourceDirectory,
    string? OutputDirectory,
    string? Split,
    bool AllowRevisionMismatch,
    bool KeepDatabase,
    string? ExpectationsPath = null);

/// <summary>
/// The tool's entry point and its <c>run</c> command: index a source directory into a throwaway database, run a dataset's
/// questions against that snapshot through the production pipeline, and write one result file.
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
          dotnet run --project tools/Trace.Evals -- report <result file> [--output <directory>]
          dotnet run --project tools/Trace.Evals -- compare <baseline result file> <candidate result file> [--output <directory>]
          dotnet run --project tools/Trace.Evals -- check <result file> --expectations <file>

        run: asks every question of a dataset against a snapshot of the source directory and
        writes one result file.

          --dataset <file>            Labeled questions (see docs/evaluation.md).
          --source <directory>        The repository to index, as a local directory.
          --output <directory>        Where the result file goes. Default: eval/results (git-ignored).
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
        """;

    public static EvalOptions ParseArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args[0] != "run")
        {
            throw new EvalUsageException("Expected a command: 'run', 'report', 'compare' or 'check'.");
        }

        string? dataset = null;
        string? source = null;
        string? output = null;
        string? split = null;
        string? expectations = null;
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

        return new EvalOptions(
            dataset ?? throw new EvalUsageException("'--dataset' is required."),
            source ?? throw new EvalUsageException("'--source' is required."),
            output,
            split,
            allowRevisionMismatch,
            keepDatabase,
            expectations);
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

        EvalOptions options;
        EvalDataset dataset;
        IReadOnlySet<string>? questionIds;
        try
        {
            options = ParseArguments(args);
            dataset = EvalDataset.Load(options.DatasetPath);
            questionIds = options.ExpectationsPath is null ? null : EvalExpectations.Load(options.ExpectationsPath).SelectFrom(dataset);
            if (!Directory.Exists(options.SourceDirectory))
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
            ?? Path.Combine(repositoryRoot ?? Directory.GetCurrentDirectory(), DefaultOutputDirectory));
        var (traceCommit, traceCommitDirty) = ReadTraceCommit(repositoryRoot);

        await using var database = await ThrowawayDatabase.CreateAsync(
            serverConnectionString, options.KeepDatabase, cancellationToken);

        // From here on every service of the pipeline talks to the throwaway database only.
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
        IndexResult index;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TraceDbContext>();
            await db.Database.MigrateAsync(cancellationToken);

            // Public and not from GitHub: the one kind of repository an anonymous caller may see.
            var repository = Repository.Create("trace-eval", dataset.Name, SourceBranch, RepositoryVisibility.Public);
            db.Repositories.Add(repository);
            await db.SaveChangesAsync(cancellationToken);
            repositoryId = repository.Id;

            index = await scope.ServiceProvider.GetRequiredService<SnapshotIndexer>().IndexAsync(
                repositoryId, new LocalDirectorySource(options.SourceDirectory, SourceBranch), cancellationToken);
        }

        await output.WriteLineAsync(
            $"Dataset {dataset.Name} v{dataset.Version}; snapshot {index.CommitSha[..12]} ({index.ChunkCount} passages); "
            + $"model {model.ModelId}; database {database.Name}.");

        var request = new EvalRunRequest(
            dataset,
            repositoryId,
            index.SnapshotId,
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

    private static TimeSpan QuestionTimeout(IConfiguration configuration) =>
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
