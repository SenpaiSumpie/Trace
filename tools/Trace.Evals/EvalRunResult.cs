using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Trace.Evals;

/// <summary>The fixed names a question's result can have in a result file.</summary>
public static class EvalOutcomes
{
    /// <summary>The pipeline produced an answer backed by at least one validated citation.</summary>
    public const string Answered = "answered";

    /// <summary>The pipeline reported that the snapshot did not show enough to answer.</summary>
    public const string InsufficientEvidence = "insufficient_evidence";

    /// <summary>The investigation failed; <c>errorCode</c> says why.</summary>
    public const string Failed = "failed";

    /// <summary>The investigation was cancelled before it finished.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The run was cancelled before this question was started.</summary>
    public const string NotRun = "not_run";

    public static readonly IReadOnlyList<string> All = [Answered, InsufficientEvidence, Failed, Cancelled, NotRun];
}

/// <summary>A result file cannot be read or scored; the message says why.</summary>
public sealed class EvalResultFileException(string message) : Exception(message);

public static class EvalRunStatus
{
    public const string Completed = "completed";

    /// <summary>The run was stopped early; questions that were never started are <c>not_run</c>.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One evaluation run, exactly as it is written to a result file. The format is documented in
/// <c>docs/evaluation.md</c>; change <see cref="CurrentSchemaVersion"/> with any change to it.
/// </summary>
/// <param name="TraceCommit">The commit of TRACE itself that ran the questions, or "unknown".</param>
/// <param name="TraceCommitDirty">Whether the working tree had uncommitted changes; null if unknown.</param>
/// <param name="Prices">The prices the cost estimates used; null if none were configured for the model.</param>
public sealed record EvalRunResult(
    int SchemaVersion,
    string RunId,
    string Status,
    string TraceCommit,
    bool? TraceCommitDirty,
    EvalDatasetInfo Dataset,
    EvalSnapshotInfo Snapshot,
    EvalChatInfo Chat,
    EvalPriceInfo? Prices,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    EvalTotals Totals,
    IReadOnlyList<EvalQuestionResult> Questions)
{
    /// <summary>2 added each question's <c>retrieval</c> (the locations recall at 10 is computed from).</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The oldest format <see cref="Load"/> still reads. A version 1 file has no <c>retrieval</c>.</summary>
    public const int OldestReadableSchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,

        // Evidence excerpts are source code; the default encoder would turn every quote and
        // angle bracket into a \uXXXX escape. The file is read as JSON, never pasted into HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static EvalRunResult FromJson(string json) =>
        JsonSerializer.Deserialize<EvalRunResult>(json, Json)
        ?? throw new JsonException("The result file is empty.");

    /// <summary>
    /// Reads a result file for scoring. Refuses a file of an unknown format version or one that
    /// lacks what every metric relies on, rather than scoring it as if it were complete.
    /// </summary>
    /// <exception cref="EvalResultFileException">The file cannot be read or is not a usable result file.</exception>
    public static EvalRunResult Load(string path)
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
            throw new EvalResultFileException($"'{path}' is not a usable result file: {exception.Message}");
        }
    }

    /// <summary>As <see cref="Load"/>, from the file's text.</summary>
    public static EvalRunResult Parse(string json)
    {
        EvalRunResult? result;
        try
        {
            result = JsonSerializer.Deserialize<EvalRunResult>(json, Json);
        }
        catch (JsonException exception)
        {
            throw new EvalResultFileException($"it is not JSON of the documented shape ({exception.Message})");
        }

        if (result is null)
        {
            throw new EvalResultFileException("it is empty.");
        }

        if (result.SchemaVersion is < OldestReadableSchemaVersion or > CurrentSchemaVersion)
        {
            throw new EvalResultFileException(
                $"its schemaVersion is {result.SchemaVersion}; this tool reads versions {OldestReadableSchemaVersion} to {CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(result.RunId) || result.Dataset is null || result.Snapshot is null || result.Chat is null)
        {
            throw new EvalResultFileException("\"runId\", \"dataset\", \"snapshot\" and \"chat\" are required.");
        }

        if (result.Questions is null)
        {
            throw new EvalResultFileException("\"questions\" is required.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var questions = new List<EvalQuestionResult>(result.Questions.Count);
        foreach (var question in result.Questions)
        {
            if (question is null || string.IsNullOrWhiteSpace(question.Id))
            {
                throw new EvalResultFileException("every question needs an \"id\".");
            }

            if (!ids.Add(question.Id))
            {
                throw new EvalResultFileException($"question id '{question.Id}' appears more than once.");
            }

            if (question.Split is not (EvalDataset.DevSplit or EvalDataset.TestSplit))
            {
                throw new EvalResultFileException(
                    $"question '{question.Id}' must have \"split\" \"{EvalDataset.DevSplit}\" or \"{EvalDataset.TestSplit}\".");
            }

            if (!EvalOutcomes.All.Contains(question.Outcome))
            {
                throw new EvalResultFileException($"question '{question.Id}' has an unknown \"outcome\".");
            }

            if (question.Tokens is null)
            {
                throw new EvalResultFileException($"question '{question.Id}' has no \"tokens\".");
            }

            // A list left out of a hand-written file reads as null; score it as empty.
            questions.Add(question with
            {
                Expected = question.Expected ?? [],
                Evidence = question.Evidence ?? [],
                Unknowns = question.Unknowns ?? [],
                Limitations = question.Limitations ?? [],
                ToolCalls = question.ToolCalls ?? [],
                Retrieval = question.Retrieval is { } retrieval ? retrieval with { Hits = retrieval.Hits ?? [] } : null,
            });
        }

        return result with { Questions = questions };
    }

    /// <summary>Sortable and unique: dataset, UTC start time, then the start of the run ID.</summary>
    public string FileName() =>
        $"{Dataset.Name}-{StartedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{RunId[..8]}.json";

    /// <summary>Writes the result into the directory (created if needed) and returns the file's path.</summary>
    public async Task<string> WriteAsync(string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName());
        await File.WriteAllTextAsync(path, ToJson() + "\n", cancellationToken);
        return path;
    }
}

/// <param name="Revision">The revision the dataset says its labels were written against, if any.</param>
/// <param name="RevisionMatchesSnapshot">Null when the dataset states no revision.</param>
/// <param name="Split">The split the run was limited to, or "all".</param>
public sealed record EvalDatasetInfo(
    string Name,
    string Version,
    string? Revision,
    bool? RevisionMatchesSnapshot,
    string Split,
    int QuestionCount);

/// <param name="CommitSha">The indexed revision every question was pinned to.</param>
/// <param name="ChunkerVersion">The chunker that built the snapshot (its recorded parser version).</param>
public sealed record EvalSnapshotInfo(
    string CommitSha,
    string Branch,
    string ChunkerVersion,
    string EmbeddingProvider,
    string EmbeddingModel);

public sealed record EvalChatInfo(string Provider, string Model, string PromptVersion);

public sealed record EvalPriceInfo(
    string Currency,
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheReadPerMillion,
    decimal CacheWritePerMillion);

/// <summary>Counts and sums over the questions. Quality metrics are computed from the file, not stored in it.</summary>
public sealed record EvalTotals(
    int Questions,
    int Answered,
    int InsufficientEvidence,
    int Failed,
    int Cancelled,
    int NotRun,
    EvalTokens Tokens,
    decimal? EstimatedCostUsd);

/// <param name="Input">Every input token, including the two cached kinds.</param>
public sealed record EvalTokens(long Input, long Output, long CacheRead, long CacheWrite);

/// <summary>A citation that passed validation: it is in the snapshot and was retrieved in the same investigation.</summary>
public sealed record EvalEvidence(string Path, int StartLine, int EndLine, string Supports, string Excerpt);

/// <param name="Seq">Position among the investigation's recorded steps.</param>
/// <param name="Tool">The tool name; null when the model asked for a tool that does not exist.</param>
/// <param name="Status"><c>called</c>, <c>failed</c> (the tool returned an error) or <c>rejected</c> (refused without running).</param>
/// <param name="ResultCount">Search hits or lines returned; null unless the call succeeded.</param>
public sealed record EvalToolCall(
    int Seq,
    string? Tool,
    string Status,
    int? ResultCount,
    long? LatencyMs,
    int? OutputChars,
    string? ErrorCode,
    string Summary)
{
    public const string Called = "called";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
}

/// <summary>One passage the search returned for a question: where it is, never its text.</summary>
/// <param name="Rank">1 is the best hit.</param>
/// <param name="LexicalRank">Its rank in the full-text search alone; null if that search did not return it.</param>
/// <param name="VectorRank">Its rank in the embedding search alone; null if that search did not return it.</param>
public sealed record EvalRetrievedLocation(int Rank, string Path, int StartLine, int EndLine, int? LexicalRank, int? VectorRank);

/// <summary>
/// What the production search returned when asked the question itself, word for word. Recall
/// at 10 is computed from this; it is measured apart from the investigation and never shown
/// to the model.
/// </summary>
/// <param name="Limit">How many hits were asked for.</param>
/// <param name="Hits">In rank order. Empty when the search failed.</param>
/// <param name="ErrorCode">Why the search could not be made; null when it was.</param>
public sealed record EvalRetrieval(int Limit, IReadOnlyList<EvalRetrievedLocation> Hits, string? ErrorCode)
{
    /// <summary>The search stopped with an error the tool does not name (for example the embedding provider was unreachable).</summary>
    public const string FailedError = "retrieval_failed";

    public const string TimedOutError = "timed_out";
}

/// <param name="Retrieval">
/// Null when retrieval was not measured: the pipeline never accepted the question, the run was
/// stopped, or the file predates schema version 2.
/// </param>
/// <param name="Outcome">One of <see cref="EvalOutcomes"/>.</param>
/// <param name="ErrorCode">The pipeline's reason for a failure; null otherwise.</param>
/// <param name="InvestigationId">The investigation in the run's database; null if none was created.</param>
/// <param name="Answer">The stored answer; null unless the investigation completed.</param>
/// <param name="RejectedCitations">Citations the model claimed that validation discarded.</param>
/// <param name="EstimatedCostUsd">Null when no prices are configured for the model.</param>
public sealed record EvalQuestionResult(
    string Id,
    string Split,
    string Question,
    bool Answerable,
    IReadOnlyList<EvalRange> Expected,
    EvalRetrieval? Retrieval,
    string Outcome,
    string? ErrorCode,
    Guid? InvestigationId,
    string? Answer,
    IReadOnlyList<EvalEvidence> Evidence,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<EvalToolCall> ToolCalls,
    int RejectedCitations,
    EvalTokens Tokens,
    decimal? EstimatedCostUsd,
    long DurationMs);
