using System.Globalization;
using System.Text;

namespace Trace.Evals;

/// <summary>
/// Renders a report and a comparison as Markdown. Only identifiers, counts, file paths and
/// numbers are written: never a question, an answer or source text. Every value that comes
/// from a result file is written as a code span with the characters that could break out of
/// it or out of a table removed, because a result file can be edited by anyone and file paths
/// come from the evaluated repository.
/// </summary>
public static class EvalMarkdown
{
    public const string NotApplicable = "n/a";
    public const int MaxValueLength = 200;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Report(EvalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();
        text.AppendLine(Invariant, $"# Evaluation report: {Code(report.Run.Dataset.Name)} version {Code(report.Run.Dataset.Version)}");
        text.AppendLine();
        text.AppendLine("| | |");
        text.AppendLine("|---|---|");
        foreach (var (name, value) in RunRows(report.Run))
        {
            text.AppendLine(Invariant, $"| {name} | {value} |");
        }

        AppendList(text, "Read this first", report.Notes);

        text.AppendLine();
        text.AppendLine("## Metrics");
        text.AppendLine();
        text.AppendLine("Definitions are in `docs/evaluation.md`. `n/a` means there was nothing to measure, not zero.");
        text.AppendLine();
        text.AppendLine("| Metric | All | Development (`dev`) | Held-out (`test`) |");
        text.AppendLine("|---|---|---|---|");
        foreach (var (name, value) in MetricRows())
        {
            text.AppendLine(Invariant, $"| {name} | {string.Join(" | ", report.Metrics.Select(value))} |");
        }

        text.AppendLine();
        text.AppendLine("## Questions");
        text.AppendLine();
        if (report.Questions.Count == 0)
        {
            text.AppendLine("The result file has no questions.");
            return text.ToString();
        }

        text.AppendLine("| Question | Split | Outcome | Recall at 10 | Cites a labeled location | Valid citations | Evidence files | Cost | Time |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var question in report.Questions)
        {
            text.AppendLine(
                Invariant,
                $"| {Code(question.Id)} | {Code(question.Split)} | {Outcome(question)} | {Recall(question)} | {YesNo(question.CitesLabeledEvidence)} "
                + $"| {Citations(question)} | {Files(question.EvidenceFiles)} | {Money(question.RanToAnEnd ? question.EstimatedCostUsd : null)} "
                + $"| {(question.RanToAnEnd ? Duration(question.DurationMs) : NotApplicable)} |");
        }

        return text.ToString();
    }

    public static string Comparison(EvalComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var text = new StringBuilder();
        text.AppendLine("# Evaluation comparison");
        text.AppendLine();
        text.AppendLine("| | Baseline | Candidate |");
        text.AppendLine("|---|---|---|");
        var candidateRows = RunRows(comparison.Candidate);
        foreach (var ((name, baseline), index) in RunRows(comparison.Baseline).Select((row, index) => (row, index)))
        {
            var candidate = candidateRows[index].Value;
            // Two runs always differ in their ID and start time; anything else that differs is worth a look.
            var differs = index >= 2 && baseline != candidate;
            text.AppendLine(Invariant, $"| {name} | {baseline} | {candidate}{(differs ? " **(differs)**" : string.Empty)} |");
        }

        AppendList(text, "Not like for like", comparison.Warnings);

        var counts = comparison.Counts;
        text.AppendLine();
        text.AppendLine("## Summary");
        text.AppendLine();
        text.AppendLine(
            Invariant,
            $"{counts.Regressed} regressed, {counts.Improved} improved, {counts.Changed} changed otherwise, {counts.Unchanged} unchanged, "
            + $"{counts.OnlyInBaseline} only in the baseline, {counts.OnlyInCandidate} only in the candidate.");
        text.AppendLine();
        text.AppendLine(
            "A question regressed if it lost the outcome its label calls for, a labeled location dropped out of the top 10, "
            + "its evidence stopped resting on a labeled location, or a smaller share of its citations was valid. "
            + "Cost and time are shown but never decide. Definitions are in `docs/evaluation.md`.");

        text.AppendLine();
        text.AppendLine("## Metrics");
        foreach (var (baseline, index) in comparison.BaselineMetrics.Select((set, index) => (set, index)))
        {
            var candidate = comparison.CandidateMetrics[index];
            text.AppendLine();
            text.AppendLine(Invariant, $"### {ScopeName(baseline.Scope)}");
            text.AppendLine();
            text.AppendLine("| Metric | Baseline | Candidate |");
            text.AppendLine("|---|---|---|");
            foreach (var (name, value) in MetricRows())
            {
                text.AppendLine(Invariant, $"| {name} | {value(baseline)} | {value(candidate)} |");
            }
        }

        text.AppendLine();
        text.AppendLine("## Questions");
        text.AppendLine();
        if (comparison.Questions.Count == 0)
        {
            text.AppendLine("Neither file has any question.");
            return text.ToString();
        }

        text.AppendLine("Regressions first. Each cell is baseline -> candidate; one value means no change.");
        text.AppendLine();
        text.AppendLine("| Question | Split | Status | Outcome | Recall at 10 | Cites a labeled location | Valid citations | Cost | Time |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var change in comparison.Questions)
        {
            text.AppendLine(
                Invariant,
                $"| {Code(change.Id)} | {Code(change.Split)} | {Status(change.Status)} | {Pair(change, Outcome)} | {Pair(change, Recall)} "
                + $"| {Pair(change, score => YesNo(score.CitesLabeledEvidence))} | {Pair(change, Citations)} "
                + $"| {Pair(change, score => Money(score.RanToAnEnd ? score.EstimatedCostUsd : null))} "
                + $"| {Pair(change, score => score.RanToAnEnd ? Duration(score.DurationMs) : NotApplicable)} |");
        }

        var details = comparison.Questions
            .Where(change => change.Status is EvalChangeStatus.Regressed or EvalChangeStatus.Improved or EvalChangeStatus.Changed)
            .ToList();
        if (details.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## What changed");
            foreach (var change in details)
            {
                text.AppendLine();
                text.AppendLine(Invariant, $"### {Code(change.Id)}: {Status(change.Status)}");
                text.AppendLine();
                AppendLines(text, "Worse", change.Regressions);
                AppendLines(text, "Better", change.Improvements);
                AppendLines(text, "Different", change.OtherChanges);
                if (change.EvidenceFilesAdded.Count > 0)
                {
                    text.AppendLine(Invariant, $"- Evidence files added: {Files(change.EvidenceFilesAdded)}");
                }

                if (change.EvidenceFilesRemoved.Count > 0)
                {
                    text.AppendLine(Invariant, $"- Evidence files removed: {Files(change.EvidenceFilesRemoved)}");
                }

                if (change.CostDeltaUsd is { } cost && cost != 0)
                {
                    text.AppendLine(Invariant, $"- Cost: {(cost > 0 ? "+" : "-")}{Money(Math.Abs(cost))}");
                }

                if (change.DurationDeltaMs is { } duration && duration != 0)
                {
                    text.AppendLine(Invariant, $"- Time: {(duration >= 0 ? "+" : "-")}{Duration(Math.Abs(duration))}");
                }
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// A value from a result file as an inline code span that cannot end early, start a new
    /// table cell or a new line, however the value was written.
    /// </summary>
    public static string Code(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return NotApplicable;
        }

        var cleaned = new StringBuilder(Math.Min(value.Length, MaxValueLength));
        foreach (var character in value)
        {
            if (cleaned.Length == MaxValueLength)
            {
                cleaned.Append('…');
                break;
            }

            cleaned.Append(character switch
            {
                '`' => '\'',
                '|' => '¦',
                _ when char.IsControl(character) || char.IsSeparator(character) => ' ',
                _ => character,
            });
        }

        var text = cleaned.ToString().Trim();
        return text.Length == 0 ? NotApplicable : $"`{text}`";
    }

    public static string Percent(double? value) =>
        value is { } number ? (number * 100).ToString("0.0", Invariant) + "%" : NotApplicable;

    public static string Money(decimal? value) =>
        value is { } number ? "$" + number.ToString("0.0000", Invariant) : NotApplicable;

    public static string Duration(double? milliseconds) =>
        milliseconds switch
        {
            null => NotApplicable,
            < 1_000 => milliseconds.Value.ToString("0.#", Invariant) + " ms",
            _ => (milliseconds.Value / 1_000).ToString("0.00", Invariant) + " s",
        };

    private static (string Name, string Value)[] RunRows(EvalRunSummary run) =>
    [
        ("Run", $"{Code(run.RunId)} ({Code(run.Status)})"),
        ("Started (UTC)", Code(run.StartedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", Invariant))),
        ("TRACE commit", Code(run.TraceCommit) + (run.TraceCommitDirty == true ? " with uncommitted changes" : string.Empty)),
        ("Dataset", $"{Code(run.Dataset.Name)} version {Code(run.Dataset.Version)}, split {Code(run.Dataset.Split)}, {run.Dataset.QuestionCount} question(s)"),
        ("Source revision", Code(run.Snapshot.CommitSha)),
        ("Chunker", Code(run.Snapshot.ChunkerVersion)),
        ("Embeddings", $"{Code(run.Snapshot.EmbeddingProvider)} / {Code(run.Snapshot.EmbeddingModel)}"),
        ("Chat model", $"{Code(run.Chat.Provider)} / {Code(run.Chat.Model)}"),
        ("Prompt version", Code(run.Chat.PromptVersion)),
        ("Result format", run.SchemaVersion.ToString(Invariant)),
    ];

    private static (string Name, Func<EvalMetricSet, string> Value)[] MetricRows() =>
    [
        ("Questions", set => set.Questions.ToString(Invariant)),
        ("Answered / insufficient evidence / failed / cancelled / not run", set =>
            $"{set.Outcomes.Answered} / {set.Outcomes.InsufficientEvidence} / {set.Outcomes.Failed} / {set.Outcomes.Cancelled} / {set.Outcomes.NotRun}"),
        ("Retrieval recall at 10", set => Share(set.RecallAt10.Value, set.RecallAt10.Found, set.RecallAt10.Labeled, "labeled locations")),
        ("Recall: questions with every location found", set =>
            set.RecallAt10.QuestionsMeasured == 0 ? NotApplicable : $"{set.RecallAt10.QuestionsAllFound} of {set.RecallAt10.QuestionsMeasured}"),
        ("Recall: answerable questions not measured", set => set.RecallAt10.QuestionsNotMeasured.ToString(Invariant)),
        ("Citation validity", set => Share(set.CitationValidity.Value, set.CitationValidity.Valid, set.CitationValidity.Cited, "citations")),
        ("Abstention precision", set => Share(set.Abstention.Precision, set.Abstention.Correct, set.Abstention.Abstained, "abstentions")),
        ("Abstention recall", set => Share(set.Abstention.Recall, set.Abstention.Correct, set.Abstention.Unanswerable, "unanswerable questions")),
        ("Answers citing a labeled location", set =>
            Share(set.LabeledEvidenceCited.Value, set.LabeledEvidenceCited.Cited, set.LabeledEvidenceCited.Questions, "answerable questions")),
        ("Cost: total", set => Money(set.Cost.TotalUsd)),
        ("Cost per question: median", set => Money(set.Cost.MedianUsd)),
        ("Cost per question: 95th percentile", set => Money(set.Cost.P95Usd)),
        ("Time per question: median", set => Duration(set.Duration.MedianMs)),
        ("Time per question: 95th percentile", set => Duration(set.Duration.P95Ms)),
    ];

    private static string Share(double? value, int part, int whole, string unit) =>
        $"{Percent(value)} ({part} of {whole} {unit})";

    private static string ScopeName(string scope) =>
        scope switch
        {
            EvalDataset.DevSplit => "Development split (`dev`)",
            EvalDataset.TestSplit => "Held-out split (`test`)",
            _ => "All questions",
        };

    private static string Outcome(EvalQuestionScore score) =>
        score.ErrorCode is null ? Code(score.Outcome) : $"{Code(score.Outcome)} ({Code(score.ErrorCode)})";

    private static string Recall(EvalQuestionScore score) =>
        score.RecallFound is { } found ? $"{found} of {score.RecallLabeled}" : NotApplicable;

    private static string Citations(EvalQuestionScore score) =>
        $"{score.ValidCitations} of {score.ValidCitations + score.RejectedCitations}";

    private static string YesNo(bool? value) => value switch { true => "yes", false => "no", null => NotApplicable };

    private static string Files(IReadOnlyList<string> files) =>
        files.Count == 0 ? "none" : string.Join(", ", files.Select(Code));

    private static string Status(string status) =>
        status switch
        {
            EvalChangeStatus.Regressed => "**regressed**",
            EvalChangeStatus.Improved => "improved",
            EvalChangeStatus.Changed => "changed",
            EvalChangeStatus.OnlyInBaseline => "only in baseline",
            EvalChangeStatus.OnlyInCandidate => "only in candidate",
            _ => "unchanged",
        };

    private static string Pair(EvalQuestionChange change, Func<EvalQuestionScore, string> value)
    {
        var before = change.Baseline is null ? "absent" : value(change.Baseline);
        var after = change.Candidate is null ? "absent" : value(change.Candidate);
        return before == after ? before : $"{before} -> {after}";
    }

    private static void AppendList(StringBuilder text, string heading, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine(Invariant, $"**{heading}:**");
        text.AppendLine();
        foreach (var line in lines)
        {
            text.AppendLine(Invariant, $"- {line}");
        }
    }

    private static void AppendLines(StringBuilder text, string label, IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            // These lines are built from counts and from outcome and error names, which a file controls.
            text.AppendLine(Invariant, $"- {label}: {Code(line)}");
        }
    }
}
