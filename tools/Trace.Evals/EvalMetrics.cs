using System.Text.Encodings.Web;
using System.Text.Json;

namespace Trace.Evals;

/// <param name="Value">Found / labeled; null when no labeled location was measured.</param>
/// <param name="Found">Labeled locations that a top-10 hit overlaps.</param>
/// <param name="Labeled">Labeled locations of the questions that were measured.</param>
/// <param name="QuestionsMeasured">Answerable questions with a recorded retrieval.</param>
/// <param name="QuestionsNotMeasured">Answerable questions without one; they are in neither count above.</param>
/// <param name="QuestionsAllFound">Measured questions whose every labeled location was found.</param>
public sealed record RecallMetric(
    double? Value,
    int Found,
    int Labeled,
    int QuestionsMeasured,
    int QuestionsNotMeasured,
    int QuestionsAllFound);

/// <param name="Value">Valid / cited; null when the model cited nothing.</param>
/// <param name="Valid">Citations that passed the pipeline's checks and became evidence.</param>
/// <param name="Cited">Valid citations plus the ones the pipeline discarded.</param>
public sealed record CitationMetric(double? Value, int Valid, int Cited);

/// <param name="Precision">Correct / abstained; null when nothing was abstained on.</param>
/// <param name="Recall">Correct / unanswerable; null when there is no unanswerable question that ran.</param>
/// <param name="Correct">Unanswerable questions that ended as "insufficient evidence".</param>
/// <param name="Abstained">Every question that ended as "insufficient evidence".</param>
/// <param name="Unanswerable">Unanswerable questions that ran to an end, a failure included.</param>
public sealed record AbstentionMetric(double? Precision, double? Recall, int Correct, int Abstained, int Unanswerable);

/// <param name="Value">Cited / questions; null when no answerable question ran to an end.</param>
/// <param name="Cited">Answerable questions whose validated evidence overlaps a labeled location.</param>
/// <param name="Questions">Answerable questions that ran to an end.</param>
public sealed record LabeledEvidenceMetric(double? Value, int Cited, int Questions);

/// <param name="Questions">Questions that ran to an end and have a cost estimate.</param>
public sealed record CostMetric(int Questions, decimal? TotalUsd, decimal? MedianUsd, decimal? P95Usd);

/// <param name="Questions">Questions that ran to an end.</param>
public sealed record DurationMetric(int Questions, double? MedianMs, double? P95Ms);

public sealed record OutcomeCounts(int Answered, int InsufficientEvidence, int Failed, int Cancelled, int NotRun);

/// <summary>Every metric over one set of questions.</summary>
/// <param name="Scope"><c>all</c>, <c>dev</c> or <c>test</c>.</param>
public sealed record EvalMetricSet(
    string Scope,
    int Questions,
    OutcomeCounts Outcomes,
    RecallMetric RecallAt10,
    CitationMetric CitationValidity,
    AbstentionMetric Abstention,
    LabeledEvidenceMetric LabeledEvidenceCited,
    CostMetric Cost,
    DurationMetric Duration);

/// <summary>One question, reduced to what the metrics and a comparison of two runs use.</summary>
/// <param name="RecallFound">Null when recall was not measured for this question.</param>
/// <param name="CitesLabeledEvidence">Null for an unanswerable question or one that did not run to an end.</param>
/// <param name="EvidenceFiles">The distinct files of the validated evidence, sorted.</param>
public sealed record EvalQuestionScore(
    string Id,
    string Split,
    bool Answerable,
    string Outcome,
    string? ErrorCode,
    int? RecallFound,
    int? RecallLabeled,
    bool? CitesLabeledEvidence,
    IReadOnlyList<string> EvidenceFiles,
    int ValidCitations,
    int RejectedCitations,
    decimal? EstimatedCostUsd,
    long DurationMs)
{
    /// <summary>The investigation reached an end of its own: an answer, an abstention or a failure.</summary>
    public bool RanToAnEnd => Outcome is EvalOutcomes.Answered or EvalOutcomes.InsufficientEvidence or EvalOutcomes.Failed;

    /// <summary>"Answered" for an answerable question, "insufficient evidence" for an unanswerable one.</summary>
    public bool HasExpectedOutcome => Outcome == (Answerable ? EvalOutcomes.Answered : EvalOutcomes.InsufficientEvidence);
}

/// <summary>Which run a report or comparison is about: the versions needed to compare like with like.</summary>
public sealed record EvalRunSummary(
    string RunId,
    int SchemaVersion,
    string Status,
    string TraceCommit,
    bool? TraceCommitDirty,
    EvalDatasetInfo Dataset,
    EvalSnapshotInfo Snapshot,
    EvalChatInfo Chat,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt)
{
    public static EvalRunSummary Of(EvalRunResult run) =>
        new(
            run.RunId, run.SchemaVersion, run.Status, run.TraceCommit, run.TraceCommitDirty,
            run.Dataset, run.Snapshot, run.Chat, run.StartedAt, run.EndedAt);
}

/// <summary>
/// The metrics of one run, as the <c>report</c> command writes them. The definitions are in
/// <c>docs/evaluation.md</c>; change <see cref="CurrentReportVersion"/> with any change to this shape.
/// </summary>
/// <param name="Metrics">Three sets, always in this order: <c>all</c>, <c>dev</c>, <c>test</c>.</param>
/// <param name="Notes">What a reader must know before trusting the numbers.</param>
public sealed record EvalReport(
    int ReportVersion,
    EvalRunSummary Run,
    IReadOnlyList<string> Notes,
    IReadOnlyList<EvalMetricSet> Metrics,
    IReadOnlyList<EvalQuestionScore> Questions)
{
    public const int CurrentReportVersion = 1;

    public string ToJson() => JsonSerializer.Serialize(this, EvalMetrics.Json);
}

/// <summary>
/// Computes the question metrics of handoff Section 11 from a result file and nothing else.
/// A metric with nothing to measure is null ("n/a"), never 0 or 100%.
/// </summary>
public static class EvalMetrics
{
    public const string AllScope = "all";
    public const string DeterministicFakeProvider = "deterministic-fake";

    /// <summary>The 10 of "recall at 10": only the best this many hits count.</summary>
    public const int RecallDepth = 10;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static EvalReport Report(EvalRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var scores = run.Questions.Select(Score).ToList();
        return new EvalReport(
            EvalReport.CurrentReportVersion,
            EvalRunSummary.Of(run),
            Notes(run),
            [
                Compute(AllScope, scores),
                Compute(EvalDataset.DevSplit, [.. scores.Where(score => score.Split == EvalDataset.DevSplit)]),
                Compute(EvalDataset.TestSplit, [.. scores.Where(score => score.Split == EvalDataset.TestSplit)]),
            ],
            scores);
    }

    public static EvalQuestionScore Score(EvalQuestionResult question)
    {
        ArgumentNullException.ThrowIfNull(question);

        var labeled = question.Expected.Count > 0;
        var ranToAnEnd = question.Outcome is EvalOutcomes.Answered or EvalOutcomes.InsufficientEvidence or EvalOutcomes.Failed;

        int? recallFound = null;
        if (labeled && question.Retrieval is { ErrorCode: null } retrieval)
        {
            var top = retrieval.Hits.OrderBy(hit => hit.Rank).Take(RecallDepth).ToList();
            recallFound = question.Expected.Count(expected =>
                top.Any(hit => Overlaps(expected, hit.Path, hit.StartLine, hit.EndLine)));
        }

        return new EvalQuestionScore(
            question.Id,
            question.Split,
            question.Answerable,
            question.Outcome,
            question.ErrorCode,
            recallFound,
            recallFound is null ? null : question.Expected.Count,
            labeled && ranToAnEnd
                ? question.Evidence.Any(item => question.Expected.Any(expected => Overlaps(expected, item.Path, item.StartLine, item.EndLine)))
                : null,
            [.. question.Evidence.Select(item => item.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            question.Evidence.Count,
            Math.Max(0, question.RejectedCitations),
            question.EstimatedCostUsd,
            question.DurationMs);
    }

    /// <summary>
    /// A labeled location counts as found by a passage of the same file that shares at least
    /// one line with it. Paths are compared exactly, as the index stores them.
    /// </summary>
    public static bool Overlaps(EvalRange expected, string path, int startLine, int endLine)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return string.Equals(expected.Path, path, StringComparison.Ordinal)
            && startLine <= expected.EndLine
            && endLine >= expected.StartLine;
    }

    public static EvalMetricSet Compute(string scope, IReadOnlyList<EvalQuestionScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        var measured = scores.Where(score => score.RecallFound is not null).ToList();
        var found = measured.Sum(score => score.RecallFound!.Value);
        var labeled = measured.Sum(score => score.RecallLabeled!.Value);

        var valid = scores.Sum(score => score.ValidCitations);
        var cited = valid + scores.Sum(score => score.RejectedCitations);

        var abstained = scores.Count(score => score.Outcome == EvalOutcomes.InsufficientEvidence);
        var correct = scores.Count(score => !score.Answerable && score.Outcome == EvalOutcomes.InsufficientEvidence);
        var unanswerable = scores.Count(score => !score.Answerable && score.RanToAnEnd);

        var grounded = scores.Where(score => score.CitesLabeledEvidence is not null).ToList();
        var groundedHits = grounded.Count(score => score.CitesLabeledEvidence == true);

        var finished = scores.Where(score => score.RanToAnEnd).ToList();
        var costs = finished
            .Where(score => score.EstimatedCostUsd is not null)
            .Select(score => score.EstimatedCostUsd!.Value)
            .Order()
            .ToList();
        var durations = finished.Select(score => (double)score.DurationMs).Order().ToList();

        return new EvalMetricSet(
            scope,
            scores.Count,
            new OutcomeCounts(
                scores.Count(score => score.Outcome == EvalOutcomes.Answered),
                abstained,
                scores.Count(score => score.Outcome == EvalOutcomes.Failed),
                scores.Count(score => score.Outcome == EvalOutcomes.Cancelled),
                scores.Count(score => score.Outcome == EvalOutcomes.NotRun)),
            new RecallMetric(
                Ratio(found, labeled),
                found,
                labeled,
                measured.Count,
                scores.Count(score => score.Answerable && score.RecallFound is null),
                measured.Count(score => score.RecallFound == score.RecallLabeled)),
            new CitationMetric(Ratio(valid, cited), valid, cited),
            new AbstentionMetric(Ratio(correct, abstained), Ratio(correct, unanswerable), correct, abstained, unanswerable),
            new LabeledEvidenceMetric(Ratio(groundedHits, grounded.Count), groundedHits, grounded.Count),
            new CostMetric(
                costs.Count,
                costs.Count == 0 ? null : costs.Sum(),
                costs.Count == 0 ? null : (costs[(costs.Count - 1) / 2] + costs[costs.Count / 2]) / 2,
                costs.Count == 0 ? null : costs[NearestRank(costs.Count, 0.95)]),
            new DurationMetric(
                durations.Count,
                durations.Count == 0 ? null : (durations[(durations.Count - 1) / 2] + durations[durations.Count / 2]) / 2,
                durations.Count == 0 ? null : durations[NearestRank(durations.Count, 0.95)]));
    }

    /// <summary>
    /// Index, in a sorted list of <paramref name="count"/> values, of the smallest value that at
    /// least the given fraction of the values do not exceed (the nearest-rank percentile).
    /// </summary>
    public static int NearestRank(int count, double fraction)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        // Integer arithmetic: 0.95 x 20 must be exactly 19, not 19.000000000000004.
        var perTenThousand = (long)Math.Round(fraction * 10_000);
        var rank = ((count * perTenThousand) + 9_999) / 10_000;
        return (int)Math.Clamp(rank, 1, count) - 1;
    }

    private static double? Ratio(int part, int whole) => whole == 0 ? null : (double)part / whole;

    private static List<string> Notes(EvalRunResult run)
    {
        var notes = new List<string>();
        if (run.Chat.Provider == DeterministicFakeProvider)
        {
            notes.Add(
                "The chat model is the deterministic fake: outcomes, citations and abstentions show that the plumbing works, not that answers are good.");
        }

        if (run.Snapshot.EmbeddingProvider == DeterministicFakeProvider)
        {
            notes.Add("The embeddings are the deterministic fake (bag of words): recall says nothing about retrieval with a real embedding model.");
        }

        if (run.SchemaVersion < 2)
        {
            notes.Add("This result file is schema version 1, which records no retrieval: recall at 10 is not measured.");
        }

        if (run.Status != EvalRunStatus.Completed)
        {
            notes.Add("The run was stopped early: questions that were cancelled or not run are left out of the abstention, labeled-evidence, cost and duration numbers.");
        }

        if (run.Dataset.Split != AllScope)
        {
            notes.Add("The run was limited to one split: the other split's column is empty.");
        }

        if (run.Dataset.RevisionMatchesSnapshot == false)
        {
            notes.Add("The source was not the revision the dataset was labeled against: line labels may point at the wrong lines.");
        }

        if (run.Prices is null)
        {
            notes.Add("No prices were configured for the chat model: cost is not estimated.");
        }

        if (run.Questions.Any(question => question.Answerable != (question.Expected.Count > 0)))
        {
            notes.Add("At least one question's \"answerable\" flag disagrees with its labeled locations; the file may have been edited by hand.");
        }

        return notes;
    }
}
