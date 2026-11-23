namespace Trace.Evals;

/// <param name="ResultFiles">One file for <c>report</c>; baseline then candidate for <c>compare</c>.</param>
/// <param name="OutputDirectory">Null: next to the (candidate) result file.</param>
public sealed record EvalReportOptions(string Command, IReadOnlyList<string> ResultFiles, string? OutputDirectory);

/// <summary>
/// The <c>report</c> and <c>compare</c> commands. Both only read result files: they need no
/// database, no provider and no settings, and they change nothing but the two files they write.
/// </summary>
public static class EvalReportCommand
{
    public const string Report = "report";
    public const string Compare = "compare";

    public static bool Handles(IReadOnlyList<string> args) =>
        args is [Report or Compare, ..];

    public static EvalReportOptions ParseArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!Handles(args))
        {
            throw new EvalUsageException($"Expected the '{Report}' or '{Compare}' command.");
        }

        var files = new List<string>();
        string? output = null;
        for (var index = 1; index < args.Count; index++)
        {
            if (args[index] == "--output")
            {
                output = index + 1 < args.Count
                    ? args[++index]
                    : throw new EvalUsageException("'--output' needs a value.");
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

        var expected = args[0] == Report ? 1 : 2;
        if (files.Count != expected)
        {
            throw new EvalUsageException(
                args[0] == Report
                    ? "'report' needs exactly one result file."
                    : "'compare' needs exactly two result files: the baseline, then the candidate.");
        }

        return new EvalReportOptions(args[0], files, output);
    }

    /// <summary>
    /// Prints the Markdown and writes it, with the same content as JSON, next to the result
    /// file. Exit code 0 whatever the numbers say: a regression is a finding, not a failure of
    /// the tool. 1 if the arguments are wrong or a file cannot be read or written.
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        EvalReportOptions options;
        try
        {
            options = ParseArguments(args);
        }
        catch (EvalUsageException exception)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync();
            await error.WriteLineAsync(EvalCommand.Usage);
            return EvalCommand.ExitError;
        }

        try
        {
            string markdown;
            string json;
            string baseName;
            if (options.Command == Report)
            {
                var report = EvalMetrics.Report(EvalRunResult.Load(options.ResultFiles[0]));
                markdown = EvalMarkdown.Report(report);
                json = report.ToJson();
                baseName = Path.GetFileNameWithoutExtension(options.ResultFiles[0]) + ".report";
            }
            else
            {
                var baseline = EvalRunResult.Load(options.ResultFiles[0]);
                var candidate = EvalRunResult.Load(options.ResultFiles[1]);
                var comparison = EvalComparer.Compare(baseline, candidate);
                markdown = EvalMarkdown.Comparison(comparison);
                json = comparison.ToJson();
                baseName = $"compare-{FileNamePart(baseline.RunId)}-{FileNamePart(candidate.RunId)}";
            }

            // By default beside the result file, which is where source-quoting files already
            // live: a report names the evaluated repository's files.
            var directory = Path.GetFullPath(
                options.OutputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(options.ResultFiles[^1]))!);
            Directory.CreateDirectory(directory);
            var markdownPath = Path.Combine(directory, baseName + ".md");
            var jsonPath = Path.Combine(directory, baseName + ".json");
            await File.WriteAllTextAsync(markdownPath, markdown, cancellationToken);
            await File.WriteAllTextAsync(jsonPath, json + "\n", cancellationToken);

            await output.WriteAsync(markdown);
            await output.WriteLineAsync();
            await output.WriteLineAsync($"Markdown: {markdownPath}");
            await output.WriteLineAsync($"JSON: {jsonPath}");
            return EvalCommand.ExitOk;
        }
        catch (EvalResultFileException exception)
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

    /// <summary>The start of a run ID as part of a file name: a result file must not be able to choose where output goes.</summary>
    private static string FileNamePart(string runId)
    {
        var safe = new string([.. runId.Where(char.IsAsciiLetterOrDigit).Take(8)]);
        return safe.Length == 0 ? "run" : safe;
    }
}
