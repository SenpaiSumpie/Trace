using System.Text.Json;

namespace Trace.Evals;

/// <summary>What happened to one question between two runs. Listed in a comparison in this order.</summary>
public static class EvalChangeStatus
{
    /// <summary>At least one measured property got worse, whatever else changed.</summary>
    public const string Regressed = "regressed";

    /// <summary>At least one measured property got better and none got worse.</summary>
    public const string Improved = "improved";

    /// <summary>Something differs that is neither better nor worse by the rules.</summary>
    public const string Changed = "changed";

    public const string OnlyInBaseline = "only_in_baseline";
    public const string OnlyInCandidate = "only_in_candidate";
    public const string Unchanged = "unchanged";

    public static readonly IReadOnlyList<string> Order = [Regressed, Improved, Changed, OnlyInBaseline, OnlyInCandidate, Unchanged];
}

/// <param name="Regressions">What got worse, one line each.</param>
/// <param name="Improvements">What got better, one line each.</param>
/// <param name="OtherChanges">Differences that are neither.</param>
/// <param name="Baseline">Null when the question is only in the candidate.</param>
/// <param name="Candidate">Null when the question is only in the baseline.</param>
/// <param name="CostDeltaUsd">Candidate minus baseline; null unless both ran to an end with a cost estimate.</param>
/// <param name="DurationDeltaMs">Candidate minus baseline; null unless both ran to an end.</param>
public sealed record EvalQuestionChange(
    string Id,
    string Split,
    string Status,
    IReadOnlyList<string> Regressions,
    IReadOnlyList<string> Improvements,
    IReadOnlyList<string> OtherChanges,
    EvalQuestionScore? Baseline,
    EvalQuestionScore? Candidate,
    IReadOnlyList<string> EvidenceFilesAdded,
    IReadOnlyList<string> EvidenceFilesRemoved,
    decimal? CostDeltaUsd,
    long? DurationDeltaMs);

public sealed record EvalChangeCounts(int Regressed, int Improved, int Changed, int OnlyInBaseline, int OnlyInCandidate, int Unchanged);

/// <summary>
/// Two runs side by side, as the <c>compare</c> command writes them. The rules are in
/// <c>docs/evaluation.md</c>; change <see cref="CurrentComparisonVersion"/> with any change to this shape.
/// </summary>
/// <param name="Warnings">Reasons the two runs are not like for like.</param>
/// <param name="BaselineMetrics">Over every question of the baseline run: <c>all</c>, <c>dev</c>, <c>test</c>.</param>
/// <param name="Questions">Regressions first, then in the order of <see cref="EvalChangeStatus.Order"/>.</param>
public sealed record EvalComparison(
    int ComparisonVersion,
    EvalRunSummary Baseline,
    EvalRunSummary Candidate,
    IReadOnlyList<string> Warnings,
    EvalChangeCounts Counts,
    IReadOnlyList<EvalMetricSet> BaselineMetrics,
    IReadOnlyList<EvalMetricSet> CandidateMetrics,
    IReadOnlyList<EvalQuestionChange> Questions)
{
    public const int CurrentComparisonVersion = 1;

    public string ToJson() => JsonSerializer.Serialize(this, EvalMetrics.Json);
}

/// <summary>
/// Compares two result files question by question. Questions are matched by ID. Cost and time
/// are reported as differences but never decide whether a question regressed: time varies from
/// run to run, and a change in cost is a trade-off for a person to judge.
/// </summary>
public static class EvalComparer
{
    public static EvalComparison Compare(EvalRunResult baseline, EvalRunResult candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var baselineReport = EvalMetrics.Report(baseline);
        var candidateReport = EvalMetrics.Report(candidate);
        var before = baselineReport.Questions.ToDictionary(score => score.Id, StringComparer.Ordinal);
        var after = candidateReport.Questions.ToDictionary(score => score.Id, StringComparer.Ordinal);

        // Baseline order, then the questions only the candidate has, in its order.
        var changes = baselineReport.Questions
            .Select(score => Change(score, after.GetValueOrDefault(score.Id)))
            .Concat(candidateReport.Questions
                .Where(score => !before.ContainsKey(score.Id))
                .Select(score => Change(baseline: null, score)))
            .ToList();

        // A stable sort: within one status the order above is kept.
        var ordered = changes.OrderBy(change => EvalChangeStatus.Order.ToList().IndexOf(change.Status)).ToList();

        return new EvalComparison(
            EvalComparison.CurrentComparisonVersion,
            baselineReport.Run,
            candidateReport.Run,
            Warnings(baseline, candidate),
            new EvalChangeCounts(
                Count(changes, EvalChangeStatus.Regressed),
                Count(changes, EvalChangeStatus.Improved),
                Count(changes, EvalChangeStatus.Changed),
                Count(changes, EvalChangeStatus.OnlyInBaseline),
                Count(changes, EvalChangeStatus.OnlyInCandidate),
                Count(changes, EvalChangeStatus.Unchanged)),
            baselineReport.Metrics,
            candidateReport.Metrics,
            ordered);
    }

    private static int Count(List<EvalQuestionChange> changes, string status) => changes.Count(change => change.Status == status);

    private static EvalQuestionChange Change(EvalQuestionScore? baseline, EvalQuestionScore? candidate)
    {
        if (baseline is null || candidate is null)
        {
            var only = (baseline ?? candidate)!;
            return new EvalQuestionChange(
                only.Id, only.Split,
                baseline is null ? EvalChangeStatus.OnlyInCandidate : EvalChangeStatus.OnlyInBaseline,
                [], [], [], baseline, candidate, [], [], null, null);
        }

        var regressions = new List<string>();
        var improvements = new List<string>();
        var other = new List<string>();

        // Outcome: reaching the outcome the label calls for is better than not reaching it.
        if (baseline.Outcome != candidate.Outcome || baseline.ErrorCode != candidate.ErrorCode)
        {
            var line = $"outcome: {Outcome(baseline)} -> {Outcome(candidate)}";
            Classify(baseline.HasExpectedOutcome, candidate.HasExpectedOutcome, line, regressions, improvements, other);
        }

        // Recall at 10: only when both runs measured it.
        if (baseline.RecallFound is { } foundBefore && candidate.RecallFound is { } foundAfter)
        {
            var recallBefore = Fraction(foundBefore, baseline.RecallLabeled!.Value);
            var recallAfter = Fraction(foundAfter, candidate.RecallLabeled!.Value);
            if (foundBefore != foundAfter || baseline.RecallLabeled != candidate.RecallLabeled)
            {
                var line = $"recall at 10: {foundBefore} of {baseline.RecallLabeled} -> {foundAfter} of {candidate.RecallLabeled}";
                Classify(recallBefore, recallAfter, line, regressions, improvements, other);
            }
        }
        else if ((baseline.RecallFound is null) != (candidate.RecallFound is null))
        {
            other.Add(baseline.RecallFound is null ? "recall at 10 is measured only in the candidate" : "recall at 10 is measured only in the baseline");
        }

        // Evidence resting on a labeled location: only when both runs came to an end.
        if (baseline.CitesLabeledEvidence is { } citedBefore && candidate.CitesLabeledEvidence is { } citedAfter && citedBefore != citedAfter)
        {
            if (citedAfter)
            {
                improvements.Add("now cites a labeled location");
            }
            else
            {
                regressions.Add("no longer cites a labeled location");
            }
        }

        // Citation validity: only when the model cited something in both runs.
        var citationsBefore = baseline.ValidCitations + baseline.RejectedCitations;
        var citationsAfter = candidate.ValidCitations + candidate.RejectedCitations;
        if (baseline.ValidCitations != candidate.ValidCitations || baseline.RejectedCitations != candidate.RejectedCitations)
        {
            var line = $"valid citations: {baseline.ValidCitations} of {citationsBefore} -> {candidate.ValidCitations} of {citationsAfter}";
            if (citationsBefore > 0 && citationsAfter > 0)
            {
                Classify(
                    Fraction(baseline.ValidCitations, citationsBefore), Fraction(candidate.ValidCitations, citationsAfter),
                    line, regressions, improvements, other);
            }
            else
            {
                other.Add(line);
            }
        }

        var added = candidate.EvidenceFiles.Except(baseline.EvidenceFiles, StringComparer.Ordinal).ToList();
        var removed = baseline.EvidenceFiles.Except(candidate.EvidenceFiles, StringComparer.Ordinal).ToList();
        if (added.Count > 0 || removed.Count > 0)
        {
            other.Add($"evidence files: {added.Count} added, {removed.Count} removed");
        }

        var bothEnded = baseline.RanToAnEnd && candidate.RanToAnEnd;
        return new EvalQuestionChange(
            baseline.Id,
            candidate.Split,
            regressions.Count > 0 ? EvalChangeStatus.Regressed
                : improvements.Count > 0 ? EvalChangeStatus.Improved
                : other.Count > 0 ? EvalChangeStatus.Changed
                : EvalChangeStatus.Unchanged,
            regressions,
            improvements,
            other,
            baseline,
            candidate,
            added,
            removed,
            bothEnded && baseline.EstimatedCostUsd is { } costBefore && candidate.EstimatedCostUsd is { } costAfter ? costAfter - costBefore : null,
            bothEnded ? candidate.DurationMs - baseline.DurationMs : null);
    }

    private static void Classify<T>(T before, T after, string line, List<string> regressions, List<string> improvements, List<string> other)
        where T : IComparable<T>
    {
        var direction = after.CompareTo(before);
        (direction < 0 ? regressions : direction > 0 ? improvements : other).Add(line);
    }

    /// <summary>Exact, so that 1 of 2 and 2 of 4 compare as equal.</summary>
    private static decimal Fraction(int part, int whole) => whole == 0 ? 0 : (decimal)part / whole;

    private static string Outcome(EvalQuestionScore score) =>
        score.ErrorCode is null ? score.Outcome : $"{score.Outcome} ({score.ErrorCode})";

    private static List<string> Warnings(EvalRunResult baseline, EvalRunResult candidate)
    {
        var warnings = new List<string>();
        if (baseline.Dataset.Name != candidate.Dataset.Name || baseline.Dataset.Version != candidate.Dataset.Version)
        {
            warnings.Add("The two runs used different datasets or dataset versions: questions with the same ID may not be the same question.");
        }

        if (baseline.Dataset.Split != candidate.Dataset.Split)
        {
            warnings.Add("The two runs covered different splits: the overall metrics are over different questions.");
        }

        if (baseline.Snapshot.CommitSha != candidate.Snapshot.CommitSha)
        {
            warnings.Add("The two runs indexed different revisions of the source.");
        }

        if (baseline.Status != EvalRunStatus.Completed || candidate.Status != EvalRunStatus.Completed)
        {
            warnings.Add("At least one run was stopped early.");
        }

        if (baseline.SchemaVersion < 2 || candidate.SchemaVersion < 2)
        {
            warnings.Add("At least one file is schema version 1, which records no retrieval: recall at 10 cannot be compared.");
        }

        var candidateQuestions = candidate.Questions.ToDictionary(question => question.Id, StringComparer.Ordinal);
        var relabeled = baseline.Questions.Count(question =>
            candidateQuestions.TryGetValue(question.Id, out var other)
            && (question.Question != other.Question
                || question.Answerable != other.Answerable
                || !question.Expected.SequenceEqual(other.Expected)));
        if (relabeled > 0)
        {
            warnings.Add($"{relabeled} question(s) have different text or labels in the two files although their IDs match.");
        }

        return warnings;
    }
}
