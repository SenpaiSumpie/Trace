using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Trace.Application.Plans;

namespace Trace.Evals;

/// <summary>The fixed names an issue's result can have in a plan result file.</summary>
public static class PlanEvalOutcomes
{
    /// <summary>The pipeline stored a plan with at least one step.</summary>
    public const string Planned = "planned";

    /// <summary>The pipeline stored the outcome "insufficient evidence": no plan could be grounded in the snapshot.</summary>
    public const string InsufficientEvidence = "insufficient_evidence";

    /// <summary>The investigation failed; <c>errorCode</c> says why.</summary>
    public const string Failed = "failed";

    public const string Cancelled = "cancelled";

    /// <summary>The run was stopped before this issue was started.</summary>
    public const string NotRun = "not_run";

    public static readonly IReadOnlyList<string> All = [Planned, InsufficientEvidence, Failed, Cancelled, NotRun];

    /// <summary>Stopped by the model, the pipeline or a timeout, as opposed to from outside.</summary>
    public static bool RanToAnEnd(string outcome) => outcome is Planned or InsufficientEvidence or Failed;
}

/// <summary>
/// One issue-to-plan evaluation run, exactly as it is written to a result file. The format is
/// documented in <c>docs/evaluation.md</c>; change <see cref="CurrentSchemaVersion"/> with any
/// change to it. It is a different file from a question run's (<see cref="EvalRunResult"/>):
/// <see cref="Kind"/> tells them apart.
/// </summary>
public sealed record PlanEvalRunResult(
    int SchemaVersion,
    string Kind,
    string RunId,
    string Status,
    string TraceCommit,
    bool? TraceCommitDirty,
    PlanEvalLabelsInfo Labels,
    EvalSnapshotInfo Snapshot,
    PlanEvalChatInfo Chat,
    EvalPriceInfo? Prices,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    PlanEvalTotals Totals,
    IReadOnlyList<PlanEvalIssueResult> Issues)
{
    public const int CurrentSchemaVersion = 1;
    public const string PlanKind = "plan";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,

        // Excerpts are source code; the file is read as JSON, never pasted into HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <exception cref="EvalResultFileException">The file cannot be read or is not a usable plan result file.</exception>
    public static PlanEvalRunResult Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new EvalResultFileException($"Could not read the result file '{path}': {exception.Message}");
        }

        try
        {
            return Parse(json);
        }
        catch (EvalResultFileException exception)
        {
            throw new EvalResultFileException($"'{path}' is not a usable plan result file: {exception.Message}");
        }
    }

    /// <summary>
    /// Reads a result for scoring. Refuses a file of another kind or version, and one whose
    /// stored plan is not a valid <c>plan-v1</c> document, rather than scoring it as if it were.
    /// </summary>
    public static PlanEvalRunResult Parse(string json)
    {
        PlanEvalRunResult? result;
        try
        {
            result = JsonSerializer.Deserialize<PlanEvalRunResult>(json, Json);
        }
        catch (JsonException exception)
        {
            throw new EvalResultFileException($"it is not JSON of the documented shape ({exception.Message})");
        }

        if (result is null)
        {
            throw new EvalResultFileException("it is empty.");
        }

        if (result.Kind != PlanKind)
        {
            throw new EvalResultFileException($"its \"kind\" is not \"{PlanKind}\": it is not the result of a plan run.");
        }

        if (result.SchemaVersion != CurrentSchemaVersion)
        {
            throw new EvalResultFileException($"its schemaVersion is {result.SchemaVersion}; this tool reads version {CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(result.RunId) || result.Labels is null || result.Snapshot is null || result.Chat is null || result.Issues is null)
        {
            throw new EvalResultFileException("\"runId\", \"labels\", \"snapshot\", \"chat\" and \"issues\" are required.");
        }

        var numbers = new HashSet<int>();
        var issues = new List<PlanEvalIssueResult>(result.Issues.Count);
        foreach (var issue in result.Issues)
        {
            if (issue is null || issue.Number < 1 || !numbers.Add(issue.Number))
            {
                throw new EvalResultFileException("every issue needs a \"number\" of 1 or more that appears once.");
            }

            var where = $"issue #{issue.Number}";
            if (issue.Split is not (EvalDataset.DevSplit or EvalDataset.TestSplit))
            {
                throw new EvalResultFileException($"{where} must have \"split\" \"{EvalDataset.DevSplit}\" or \"{EvalDataset.TestSplit}\".");
            }

            if (!PlanEvalOutcomes.All.Contains(issue.Outcome))
            {
                throw new EvalResultFileException($"{where} has an unknown \"outcome\".");
            }

            if (issue.Tokens is null)
            {
                throw new EvalResultFileException($"{where} has no \"tokens\".");
            }

            if (issue.Label is not { ExpectedOutcome: PlanLabels.ExpectPlanned or PlanLabels.ExpectInsufficientEvidence or PlanLabels.ExpectAny })
            {
                throw new EvalResultFileException($"{where} has no usable \"label\".");
            }

            var completed = issue.Outcome is PlanEvalOutcomes.Planned or PlanEvalOutcomes.InsufficientEvidence;
            PlanResult? plan;
            try
            {
                plan = issue.StoredPlan();
            }
            catch (PlanSchemaException exception)
            {
                throw new EvalResultFileException($"{where}: {exception.Message}");
            }

            if (completed != (plan is not null)
                || (plan is not null && (plan.Outcome == PlanOutcome.Planned) != (issue.Outcome == PlanEvalOutcomes.Planned)))
            {
                throw new EvalResultFileException($"{where}: \"plan\" must be the stored plan of a completed run, with the same outcome, and absent otherwise.");
            }

            // A list left out of a hand-written file reads as null; score it as empty.
            var label = issue.Label;
            issues.Add(issue with
            {
                Limitations = issue.Limitations ?? [],
                ToolCalls = issue.ToolCalls ?? [],
                Label = label with
                {
                    RequiredFiles = [.. (label.RequiredFiles ?? []).Select(file => file with { AnyOf = file.AnyOf ?? [] })],
                    OptionalFiles = label.OptionalFiles ?? [],
                    ForbiddenPaths = label.ForbiddenPaths ?? [],
                    ForbiddenText = label.ForbiddenText ?? [],
                    Points = label.Points ?? [],
                    Risks = label.Risks ?? [],
                    Verification = label.Verification ?? [],
                    Safety = label.Safety ?? [],
                },
            });
        }

        return result with { Issues = issues };
    }

    /// <summary>Sortable and unique: label set, "plans", UTC start time, then the start of the run ID.</summary>
    public string FileName() =>
        $"{Labels.Name}-plans-{StartedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{RunId[..8]}.json";

    /// <summary>Writes the result into the directory (created if needed) and returns the file's path.</summary>
    public async Task<string> WriteAsync(string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName());
        await File.WriteAllTextAsync(path, ToJson() + "\n", cancellationToken);
        return path;
    }
}

/// <param name="Split">The split the run was limited to, "all", or "subset" when issues were chosen by number.</param>
public sealed record PlanEvalLabelsInfo(
    string Name,
    string Version,
    string? Revision,
    bool? RevisionMatchesSnapshot,
    string Split,
    int IssueCount);

/// <param name="PromptVersion">The plan prompt's version (<see cref="PlanPrompt.Version"/>).</param>
/// <param name="PlanSchemaVersion">The version of the stored plan document (<see cref="PlanResult.CurrentSchemaVersion"/>).</param>
public sealed record PlanEvalChatInfo(string Provider, string Model, string PromptVersion, string PlanSchemaVersion);

public sealed record PlanEvalTotals(
    int Issues,
    int Planned,
    int InsufficientEvidence,
    int Failed,
    int Cancelled,
    int NotRun,
    EvalTokens Tokens,
    decimal? EstimatedCostUsd);

/// <param name="Label">The issue's label, copied from the label file.</param>
/// <param name="Outcome">One of <see cref="PlanEvalOutcomes"/>.</param>
/// <param name="ErrorCode">The pipeline's reason for a failure; null otherwise.</param>
/// <param name="IssueContentHash">The hash the pipeline recorded of the issue text it showed the model; null if the issue was never read.</param>
/// <param name="Plan">The stored <c>plan-v1</c> document, exactly as the pipeline stored it; null unless the run completed.</param>
/// <param name="Trimmed">What validation removed from the model's proposal, as the pipeline recorded it; null unless the run completed.</param>
/// <param name="Limitations">The fixed sentences the pipeline stored about the outcome. Never model text.</param>
public sealed record PlanEvalIssueResult(
    int Number,
    string Split,
    PlanIssueLabel Label,
    string Outcome,
    string? ErrorCode,
    Guid? InvestigationId,
    Guid? PlanId,
    string? IssueContentHash,
    JsonElement? Plan,
    PlanTrimmed? Trimmed,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<EvalToolCall> ToolCalls,
    EvalTokens Tokens,
    decimal? EstimatedCostUsd,
    long DurationMs)
{
    /// <summary>The stored plan, read by the production reader; null when there is none.</summary>
    /// <exception cref="PlanSchemaException">The stored document is not a valid <c>plan-v1</c> plan.</exception>
    public PlanResult? StoredPlan() =>
        Plan is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } plan ? PlanResult.FromJson(plan.GetRawText()) : null;
}
