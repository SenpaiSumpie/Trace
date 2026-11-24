using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trace.Application.Indexing;
using Trace.Application.Investigations;
using Trace.Application.Issues;
using Trace.Domain;
using Trace.Infrastructure;
using Trace.Infrastructure.Indexing;
using Trace.Infrastructure.Issues;
using Trace.Infrastructure.Persistence;

namespace Trace.Evals;

/// <param name="IssueNumbers">Run only these labeled issues; empty runs every issue (of the split).</param>
public sealed record PlanEvalOptions(
    string LabelsPath,
    string IssuesDirectory,
    string SourceDirectory,
    string? OutputDirectory,
    string? Split,
    IReadOnlyList<int> IssueNumbers,
    bool AllowRevisionMismatch,
    bool KeepDatabase);

public sealed record PlanReportOptions(string ResultFile, string? RatingsFile, string? OutputDirectory);

/// <summary>
/// Serves a run's issues from a directory of issue files, for the run's own repository and no
/// other. It is the only part of the plan pipeline the tool replaces: in the application the
/// directory is read for the demo repository only, and a run's repository is not that one.
/// The files go through the same reader, limits and cleaning as any issue.
/// </summary>
public sealed class DirectoryIssueSourceResolver(string directory) : IIssueSourceResolver
{
    /// <summary>Set once the run has created its repository; until then nothing resolves.</summary>
    public Guid? RepositoryId { get; set; }

    public Task<IIssueSource?> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IIssueSource?>(repositoryId == RepositoryId ? new FixtureIssueSource(directory) : null);
}

/// <summary>
/// The <c>plan-run</c> and <c>plan-report</c> commands: issue-to-plan evaluation
/// (<c>docs/evaluation.md</c>, ADR-019). <c>plan-run</c> indexes a source directory into a
/// throwaway database and has the production pipeline plan every labeled issue;
/// <c>plan-report</c> scores a result file against the rubric and needs nothing but files.
/// </summary>
public static class PlanEvalCommand
{
    public const string Run = "plan-run";
    public const string Report = "plan-report";

    public static bool Handles(IReadOnlyList<string> args) => args is [Run or Report, ..];

    public static PlanEvalOptions ParseRunArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? labels = null;
        string? issues = null;
        string? source = null;
        string? output = null;
        string? split = null;
        var numbers = new List<int>();
        var allowRevisionMismatch = false;
        var keepDatabase = false;

        for (var index = 1; index < args.Count; index++)
        {
            string Value() => index + 1 < args.Count
                ? args[++index]
                : throw new EvalUsageException($"'{args[index]}' needs a value.");

            switch (args[index])
            {
                case "--labels":
                    labels = Value();
                    break;
                case "--issues":
                    issues = Value();
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
                case "--issue":
                    numbers.Add(int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1
                        ? number
                        : throw new EvalUsageException("'--issue' must be an issue number."));
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

        if (split is not null && numbers.Count > 0)
        {
            throw new EvalUsageException("'--split' and '--issue' cannot be combined: '--issue' already names the issues.");
        }

        if (labels is null || issues is null || source is null)
        {
            throw new EvalUsageException("'--labels', '--issues' and '--source' are required.");
        }

        return new PlanEvalOptions(labels, issues, source, output, split, numbers, allowRevisionMismatch, keepDatabase);
    }

    public static PlanReportOptions ParseReportArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var files = new List<string>();
        string? ratings = null;
        string? output = null;
        for (var index = 1; index < args.Count; index++)
        {
            if (args[index] is "--ratings" or "--output")
            {
                var value = index + 1 < args.Count
                    ? args[index + 1]
                    : throw new EvalUsageException($"'{args[index]}' needs a value.");
                if (args[index] == "--ratings")
                {
                    ratings = value;
                }
                else
                {
                    output = value;
                }

                index++;
            }
            else if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new EvalUsageException($"Unknown argument '{args[index]}'.");
            }
            else
            {
                files.Add(args[index]);
            }
        }

        return files.Count == 1
            ? new PlanReportOptions(files[0], ratings, output)
            : throw new EvalUsageException($"'{Report}' needs exactly one plan result file.");
    }

    /// <param name="configure">Runs after the tool's own configuration and service registration (see <see cref="EvalCommand.RunAsync(string[], TextWriter, TextWriter, Action{HostApplicationBuilder}?, CancellationToken)"/>).</param>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        Action<HostApplicationBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            if (args[0] == Report)
            {
                return await ReportAsync(ParseReportArguments(args), output, cancellationToken);
            }

            var options = ParseRunArguments(args);
            var labels = PlanLabels.Load(options.LabelsPath);
            if (!Directory.Exists(options.SourceDirectory))
            {
                throw new EvalUsageException($"The source directory '{options.SourceDirectory}' does not exist.");
            }

            if (!Directory.Exists(options.IssuesDirectory))
            {
                throw new EvalUsageException($"The issues directory '{options.IssuesDirectory}' does not exist.");
            }

            var selected = await SelectAsync(options, labels, cancellationToken);
            try
            {
                return await RunAsync(options, labels, selected, output, error, configure, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await error.WriteLineAsync("Cancelled before any issue was run; no result file was written.");
                return EvalCommand.ExitCancelled;
            }
            catch (Exception exception) when (exception is not EvalUsageException)
            {
                await error.WriteLineAsync($"The evaluation run could not be completed: {exception.Message}");
                return EvalCommand.ExitError;
            }
        }
        catch (EvalUsageException exception)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync();
            await error.WriteLineAsync(EvalCommand.Usage);
            return EvalCommand.ExitError;
        }
        catch (Exception exception) when (exception is PlanLabelsException or PlanRatingsException or EvalResultFileException or IssueReadException)
        {
            await error.WriteLineAsync(exception.Message);
            return EvalCommand.ExitError;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Could not write the output: {exception.Message}");
            return EvalCommand.ExitError;
        }
    }

    /// <summary>
    /// The numbers to run, or null for all. Every issue that will be run must be in the
    /// directory before anything is created or spent: a missing file is a mistake in the setup,
    /// not a measurement.
    /// </summary>
    private static async Task<IReadOnlySet<int>?> SelectAsync(PlanEvalOptions options, PlanLabels labels, CancellationToken cancellationToken)
    {
        foreach (var number in options.IssueNumbers.Where(number => labels.Issues.All(label => label.Number != number)))
        {
            throw new PlanLabelsException($"Issue #{number} is not labeled in '{labels.Name}' version {labels.Version}.");
        }

        var source = new FixtureIssueSource(Path.GetFullPath(options.IssuesDirectory));
        foreach (var label in labels.Issues.Where(label =>
            (options.Split is null || label.Split == options.Split)
            && (options.IssueNumbers.Count == 0 || options.IssueNumbers.Contains(label.Number))))
        {
            if (await source.GetAsync(label.Number, maxComments: 1, cancellationToken) is null)
            {
                throw new PlanLabelsException($"Issue #{label.Number} is labeled but '{options.IssuesDirectory}' has no such issue.");
            }
        }

        return options.IssueNumbers.Count == 0 ? null : options.IssueNumbers.ToHashSet();
    }

    private static async Task<int> RunAsync(
        PlanEvalOptions options,
        PlanLabels labels,
        IReadOnlySet<int>? issueNumbers,
        TextWriter output,
        TextWriter error,
        Action<HostApplicationBuilder>? configure,
        CancellationToken cancellationToken)
    {
        var issueSource = new DirectoryIssueSourceResolver(Path.GetFullPath(options.IssuesDirectory));
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = EvalCommand.EnvironmentName,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(EvalCommand.SettingsFile, optional: false)
            .AddEnvironmentVariables();
        builder.Logging
            .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
        builder.Services.AddTraceInfrastructure();

        // Issues come from the directory, never from GitHub: the run's repository has no installation.
        builder.Services.AddScoped<IIssueSourceResolver>(_ => issueSource);
        builder.Services.AddSingleton<PlanEvalRunner>();
        configure?.Invoke(builder);

        var configuration = builder.Configuration;
        var serverConnectionString = configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{InfrastructureServiceCollectionExtensions.ConnectionStringName}' is not configured.");
        var timeout = EvalCommand.QuestionTimeout(configuration);

        var repositoryRoot = EvalCommand.FindRepositoryRoot(Directory.GetCurrentDirectory());
        var outputDirectory = Path.GetFullPath(options.OutputDirectory
            ?? Path.Combine(repositoryRoot ?? Directory.GetCurrentDirectory(), EvalCommand.DefaultOutputDirectory));
        var (traceCommit, traceCommitDirty) = EvalCommand.ReadTraceCommit(repositoryRoot);

        await using var database = await ThrowawayDatabase.CreateAsync(serverConnectionString, options.KeepDatabase, cancellationToken);
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
            var repository = Repository.Create("trace-eval", labels.Name, EvalCommand.SourceBranch, RepositoryVisibility.Public);
            db.Repositories.Add(repository);
            await db.SaveChangesAsync(cancellationToken);
            repositoryId = repository.Id;
            issueSource.RepositoryId = repositoryId;

            index = await scope.ServiceProvider.GetRequiredService<SnapshotIndexer>().IndexAsync(
                repositoryId, new LocalDirectorySource(options.SourceDirectory, EvalCommand.SourceBranch), cancellationToken);
        }

        await output.WriteLineAsync(
            $"Labels {labels.Name} v{labels.Version}; snapshot {index.CommitSha[..12]} ({index.ChunkCount} passages); model {model.ModelId}; database {database.Name}.");

        var request = new PlanEvalRunRequest(
            labels,
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
            issueNumbers);

        var position = 0;
        var result = await host.Services.GetRequiredService<PlanEvalRunner>().RunAsync(
            request,
            // Identifiers, counts and times only: never the issue or the plan.
            issue =>
            {
                var plan = issue.StoredPlan();
                output.WriteLine(
                    $"  {++position,3}. #{issue.Number}: {issue.Outcome}"
                    + (issue.ErrorCode is null ? string.Empty : $" ({issue.ErrorCode})")
                    + (plan is null ? string.Empty : $", {plan.Steps.Count} step(s), {plan.FilesToChange.Count} file(s), {plan.Evidence.Count} evidence")
                    + (issue.Trimmed is null ? string.Empty : $", {issue.Trimmed.Items} removed")
                    + $", {issue.ToolCalls.Count} tool call(s), {issue.Tokens.Input + issue.Tokens.Output} tokens, {issue.DurationMs} ms");
            },
            cancellationToken);

        var path = await result.WriteAsync(outputDirectory, CancellationToken.None);
        var totals = result.Totals;
        await output.WriteLineAsync(
            $"{totals.Issues} issue(s): {totals.Planned} planned, {totals.InsufficientEvidence} insufficient evidence, "
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

        return result.Status == EvalRunStatus.Completed ? EvalCommand.ExitOk : EvalCommand.ExitCancelled;
    }

    /// <summary>
    /// Scores a result file and writes four files next to it: the report as Markdown and JSON,
    /// the rating sheet, and an empty ratings file to fill in. The last two quote nothing a
    /// result file does not, but the rating sheet does quote the plans.
    /// </summary>
    private static async Task<int> ReportAsync(PlanReportOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var result = PlanEvalRunResult.Load(options.ResultFile);
        var ratings = options.RatingsFile is null ? null : PlanRatings.Load(options.RatingsFile);
        PlanEvalReport report;
        try
        {
            report = PlanRubric.Report(result, ratings);
        }
        catch (PlanRatingsException exception)
        {
            throw new PlanRatingsException($"'{options.RatingsFile}' cannot be used with '{options.ResultFile}': {exception.Message}");
        }

        var directory = Path.GetFullPath(options.OutputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(options.ResultFile))!);
        Directory.CreateDirectory(directory);
        var baseName = Path.GetFileNameWithoutExtension(options.ResultFile);
        var markdown = PlanEvalMarkdown.Report(report);
        var markdownPath = Path.Combine(directory, baseName + ".plan-report.md");
        var jsonPath = Path.Combine(directory, baseName + ".plan-report.json");
        var sheetPath = Path.Combine(directory, baseName + ".rating-sheet.md");
        var templatePath = Path.Combine(directory, baseName + ".ratings.template.json");
        await File.WriteAllTextAsync(markdownPath, markdown, cancellationToken);
        await File.WriteAllTextAsync(jsonPath, report.ToJson() + "\n", cancellationToken);
        await File.WriteAllTextAsync(sheetPath, PlanEvalMarkdown.RatingSheet(result), cancellationToken);
        await File.WriteAllTextAsync(templatePath, PlanRatings.TemplateFor(result).ToJson() + "\n", cancellationToken);

        await output.WriteAsync(markdown);
        await output.WriteLineAsync();
        await output.WriteLineAsync($"Markdown: {markdownPath}");
        await output.WriteLineAsync($"JSON: {jsonPath}");
        await output.WriteLineAsync($"Rating sheet (quotes the plans): {sheetPath}");
        await output.WriteLineAsync($"Empty ratings file: {templatePath}");
        return EvalCommand.ExitOk;
    }
}
