namespace Trace.Evals;

public sealed record EvalCheckOptions(string ResultFile, string ExpectationsFile);

/// <summary>
/// The <c>check</c> command: holds one result file against an expectations file and fails when
/// an expectation is not met. This is what makes an evaluation run able to fail a build
/// (ADR-012). Like <c>report</c> it only reads files: no database, no provider, no settings.
/// </summary>
public static class EvalCheckCommand
{
    public const string Check = "check";

    public static bool Handles(IReadOnlyList<string> args) => args is [Check, ..];

    public static EvalCheckOptions ParseArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!Handles(args))
        {
            throw new EvalUsageException($"Expected the '{Check}' command.");
        }

        var files = new List<string>();
        string? expectations = null;
        for (var index = 1; index < args.Count; index++)
        {
            if (args[index] == "--expectations")
            {
                expectations = index + 1 < args.Count
                    ? args[++index]
                    : throw new EvalUsageException("'--expectations' needs a value.");
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

        if (files.Count != 1)
        {
            throw new EvalUsageException("'check' needs exactly one result file.");
        }

        return new EvalCheckOptions(files[0], expectations ?? throw new EvalUsageException("'--expectations' is required."));
    }

    /// <summary>
    /// Exit code 0 when every expectation is met, <see cref="EvalCommand.ExitExpectationsNotMet"/>
    /// when at least one is not, 1 when the arguments are wrong or a file cannot be used.
    /// </summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        EvalCheckOptions options;
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

        EvalExpectations expectations;
        EvalRunResult run;
        try
        {
            expectations = EvalExpectations.Load(options.ExpectationsFile);
            run = EvalRunResult.Load(options.ResultFile);
        }
        catch (Exception exception) when (exception is EvalExpectationsException or EvalResultFileException)
        {
            await error.WriteLineAsync(exception.Message);
            return EvalCommand.ExitError;
        }

        var failures = expectations.Check(run);
        if (failures.Count == 0)
        {
            await output.WriteLineAsync(
                $"Check passed: {expectations.Questions.Count} question(s) of '{expectations.Dataset}' meet their expectations.");
            return EvalCommand.ExitOk;
        }

        await error.WriteLineAsync(
            $"Check FAILED: {failures.Count} expectation(s) not met for the {expectations.Questions.Count} question(s) of '{expectations.Dataset}'.");
        foreach (var failure in failures)
        {
            await error.WriteLineAsync($"  - {failure}");
        }

        return EvalCommand.ExitExpectationsNotMet;
    }
}
