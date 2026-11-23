using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trace.Evals;

/// <summary>The expectations file is missing, malformed or breaks one of the format's rules.</summary>
public sealed class EvalExpectationsException(string message) : Exception(message);

/// <summary>What one question of a run must show for the <c>check</c> command to pass.</summary>
/// <param name="Outcome">
/// The outcome the question must have. Null: it only has to reach an end of its own without
/// failing (an answer or an abstention), because which of the two is the model's doing.
/// </param>
/// <param name="LabelsRetrieved">Every labeled location must be among the 10 best hits of the question's retrieval.</param>
/// <param name="CitesLabel">The validated evidence must overlap a labeled location.</param>
public sealed record EvalQuestionExpectation(string Id, string? Outcome, bool LabelsRetrieved, bool CitesLabel);

/// <summary>
/// A fixed list of questions of one dataset and what a run must show for each. It serves two
/// commands: <c>run --expectations</c> asks only the listed questions, and <c>check</c> fails
/// when a result file does not meet them. The format is documented in <c>docs/evaluation.md</c>.
/// </summary>
/// <param name="Dataset">The name of the dataset the questions belong to.</param>
public sealed record EvalExpectations(string Dataset, IReadOnlyList<EvalQuestionExpectation> Questions)
{
    // A misspelt expectation must fail loudly: silently ignored, it would be a check that checks nothing.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static EvalExpectations Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new EvalExpectationsException($"Could not read the expectations file '{path}': {exception.Message}");
        }

        try
        {
            return Parse(json);
        }
        catch (EvalExpectationsException exception)
        {
            throw new EvalExpectationsException($"'{path}' is not a usable expectations file: {exception.Message}");
        }
    }

    public static EvalExpectations Parse(string json)
    {
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, Json);
        }
        catch (JsonException exception)
        {
            throw new EvalExpectationsException($"it is not JSON of the documented shape ({exception.Message})");
        }

        if (file is null)
        {
            throw new EvalExpectationsException("it is empty.");
        }

        if (string.IsNullOrWhiteSpace(file.Dataset))
        {
            throw new EvalExpectationsException("\"dataset\" is required: the name of the dataset the questions belong to.");
        }

        if (file.Questions is not { Count: > 0 })
        {
            throw new EvalExpectationsException("\"questions\" must list at least one question.");
        }

        var questions = new List<EvalQuestionExpectation>(file.Questions.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < file.Questions.Count; index++)
        {
            var dto = file.Questions[index];
            if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
            {
                throw new EvalExpectationsException($"questions[{index}] needs an \"id\".");
            }

            if (!ids.Add(dto.Id))
            {
                throw new EvalExpectationsException($"question id '{dto.Id}' is listed more than once.");
            }

            // Only outcomes an investigation reaches by itself can be expected: "cancelled" and
            // "not_run" are things done to a run from outside.
            if (dto.Outcome is not (null or EvalOutcomes.Answered or EvalOutcomes.InsufficientEvidence or EvalOutcomes.Failed))
            {
                throw new EvalExpectationsException(
                    $"question '{dto.Id}': \"outcome\" must be \"{EvalOutcomes.Answered}\", \"{EvalOutcomes.InsufficientEvidence}\" or \"{EvalOutcomes.Failed}\", or be left out.");
            }

            questions.Add(new EvalQuestionExpectation(dto.Id, dto.Outcome, dto.LabelsRetrieved ?? false, dto.CitesLabel ?? false));
        }

        return new EvalExpectations(file.Dataset, questions);
    }

    /// <summary>
    /// The listed questions of a dataset, in dataset order: what <c>run --expectations</c> asks.
    /// </summary>
    /// <exception cref="EvalExpectationsException">
    /// The file is about another dataset, names a question the dataset does not have, or names
    /// a held-out question.
    /// </exception>
    public IReadOnlySet<string> SelectFrom(EvalDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (!string.Equals(Dataset, dataset.Name, StringComparison.Ordinal))
        {
            throw new EvalExpectationsException(
                $"The expectations are for dataset '{Dataset}' but the dataset is '{dataset.Name}'.");
        }

        var byId = dataset.Questions.ToDictionary(question => question.Id, StringComparer.Ordinal);
        foreach (var expectation in Questions)
        {
            if (!byId.TryGetValue(expectation.Id, out var question))
            {
                throw new EvalExpectationsException(
                    $"The expectations name question '{expectation.Id}', which dataset '{dataset.Name}' version {dataset.Version} does not have.");
            }

            if (question.Split != EvalDataset.DevSplit)
            {
                throw new EvalExpectationsException(HeldOutMessage(expectation.Id));
            }
        }

        return Questions.Select(expectation => expectation.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every way the run falls short of the expectations, as one sentence each; empty when it
    /// meets them all. The sentences carry IDs, fixed names and counts only: never a question,
    /// an answer or source text.
    /// </summary>
    public IReadOnlyList<string> Check(EvalRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var failures = new List<string>();

        if (!string.Equals(Dataset, run.Dataset.Name, StringComparison.Ordinal))
        {
            failures.Add($"The expectations are for dataset '{Dataset}' but the result file is a run of '{Clean(run.Dataset.Name)}'.");
        }

        if (run.Status != EvalRunStatus.Completed)
        {
            failures.Add($"The run did not complete: its status is '{Clean(run.Status)}'.");
        }

        if (run.Dataset.RevisionMatchesSnapshot == false)
        {
            failures.Add("The source was not the revision the dataset was labeled against, so the labels may point at the wrong lines.");
        }

        var byId = run.Questions.ToDictionary(question => question.Id, StringComparer.Ordinal);
        foreach (var expectation in Questions)
        {
            if (!byId.TryGetValue(expectation.Id, out var question))
            {
                failures.Add($"{expectation.Id}: the question is not in the result file.");
                continue;
            }

            failures.AddRange(CheckQuestion(expectation, question).Select(failure => $"{expectation.Id}: {failure}"));
        }

        return failures;
    }

    private static IEnumerable<string> CheckQuestion(EvalQuestionExpectation expectation, EvalQuestionResult question)
    {
        if (question.Split != EvalDataset.DevSplit)
        {
            yield return HeldOutMessage(expectation.Id);
        }

        var outcome = question.Outcome + (question.ErrorCode is null ? string.Empty : $" ({Clean(question.ErrorCode)})");
        if (expectation.Outcome is { } expected)
        {
            if (question.Outcome != expected)
            {
                yield return $"expected the outcome '{expected}' but it was '{outcome}'.";
            }
        }
        else if (question.Outcome is not (EvalOutcomes.Answered or EvalOutcomes.InsufficientEvidence))
        {
            yield return $"expected an answer or an abstention but the outcome was '{outcome}'.";
        }

        if (question.RejectedCitations > 0)
        {
            yield return $"{question.RejectedCitations} citation(s) did not pass validation; every citation must be valid.";
        }

        var score = EvalMetrics.Score(question);
        if ((expectation.LabelsRetrieved || expectation.CitesLabel) && question.Expected.Count == 0)
        {
            yield return "an expectation about labeled locations was set, but the question has none.";
            yield break;
        }

        if (expectation.LabelsRetrieved)
        {
            if (score.RecallFound is not { } found)
            {
                yield return "retrieval was not measured"
                    + (question.Retrieval?.ErrorCode is { } error ? $" ({Clean(error)})." : ".");
            }
            else if (found != score.RecallLabeled)
            {
                yield return $"only {found} of {score.RecallLabeled} labeled location(s) are among the {EvalMetrics.RecallDepth} best hits of the search.";
            }
        }

        if (expectation.CitesLabel && score.CitesLabeledEvidence != true)
        {
            yield return "no validated evidence overlaps a labeled location.";
        }
    }

    private static string HeldOutMessage(string id) =>
        $"Question '{id}' is in the held-out '{EvalDataset.TestSplit}' split. Expectations may only name '{EvalDataset.DevSplit}' questions: "
        + "an expectation is written from a question's result, and held-out results must not shape anything.";

    /// <summary>A value from a result file, which anyone can edit, made safe to print on one line.</summary>
    private static string Clean(string value) =>
        new([.. value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.').Take(64)]);

    private sealed record FileDto(string? Dataset, string? Description, List<QuestionDto?>? Questions);

    private sealed record QuestionDto(string? Id, string? Outcome, bool? LabelsRetrieved, bool? CitesLabel, string? Notes);
}
