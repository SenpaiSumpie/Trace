using Trace.Evals;

namespace Trace.UnitTests;

/// <summary>
/// The <c>check</c> command and the expectations file behind the smoke run in CI (ADR-012),
/// on result files built by hand. That a broken expectation ends with a non-zero exit code is
/// what makes the smoke run a gate, so it is asserted here for every kind of expectation.
/// </summary>
public class EvalExpectationsTests
{
    private static readonly (string Path, int StartLine, int EndLine)[] OnLabel = [("src/A.cs", 12, 14)];
    private static readonly (string Path, int StartLine, int EndLine)[] Elsewhere = [("src/B.cs", 1, 5)];

    private static readonly EvalExpectations Strict = EvalExpectations.Parse(
        """
        {
          "dataset": "sample-repo",
          "questions": [
            { "id": "answered", "outcome": "answered", "labelsRetrieved": true, "citesLabel": true },
            { "id": "unanswerable" }
          ]
        }
        """);

    [Fact]
    public void The_checked_in_smoke_subset_names_a_few_development_questions_of_the_fixture()
    {
        var fixture = Path.Combine(RepositoryRoot(), "tests", "fixtures", "sample-repo");
        var dataset = EvalDataset.Load(fixture + EvalDataset.FileSuffix);
        var smoke = EvalExpectations.Load(fixture + ".smoke.json");

        // Small enough to stay a smoke run, and fixed: a question added to the dataset does not
        // join it unless someone lists it here.
        Assert.Equal("sample-repo", smoke.Dataset);
        Assert.Equal(
            ["pagination-offset", "page-size-rationale", "title-max-length", "webhook-attempts", "api-key-rotation", "on-call-contact"],
            smoke.Questions.Select(question => question.Id));

        // Every one exists and is a development question: SelectFrom refuses anything else.
        var selected = smoke.SelectFrom(dataset);
        Assert.Equal(smoke.Questions.Count, selected.Count);
        var byId = dataset.Questions.ToDictionary(question => question.Id);
        Assert.All(smoke.Questions, expectation =>
        {
            var question = byId[expectation.Id];
            Assert.Equal(EvalDataset.DevSplit, question.Split);

            // Nothing is claimed about an unanswerable question's outcome: the fake chat model
            // answers it, and the check must not pretend that is right or wrong.
            if (question.Answerable)
            {
                Assert.Equal(((string?)EvalOutcomes.Answered, true, true), (expectation.Outcome, expectation.LabelsRetrieved, expectation.CitesLabel));
            }
            else
            {
                Assert.Equal(((string?)null, false, false), (expectation.Outcome, expectation.LabelsRetrieved, expectation.CitesLabel));
            }
        });
        Assert.Contains(smoke.Questions, expectation => !byId[expectation.Id].Answerable);
        Assert.Contains(smoke.Questions, expectation => byId[expectation.Id].Expected.Count > 1);
    }

    [Fact]
    public void The_ci_workflow_runs_the_smoke_subset_checks_it_and_uploads_the_files_without_any_secret()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains("--expectations tests/fixtures/sample-repo.smoke.json", workflow, StringComparison.Ordinal);
        Assert.Contains("-- check ", workflow, StringComparison.Ordinal);
        Assert.Contains("-- report ", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@", workflow, StringComparison.Ordinal);

        // The smoke run uses the fake providers: no job may be handed a key or any other secret.
        Assert.DoesNotContain("secrets.", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("API_KEY", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Provider", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_run_that_meets_every_expectation_passes()
    {
        Assert.Empty(Strict.Check(PassingRun()));

        // An unanswerable question with no expected outcome passes whether the model answered
        // or abstained; questions the file does not list are not looked at.
        var abstained = EvalFiles.Run(
            PassingRun().Questions[0],
            EvalFiles.Question("unanswerable", answerable: false, outcome: EvalOutcomes.InsufficientEvidence),
            EvalFiles.Question("not-listed", outcome: EvalOutcomes.Failed, errorCode: "model_error"));
        Assert.Empty(Strict.Check(abstained));
    }

    [Fact]
    public void Each_broken_expectation_is_reported_with_the_question_and_the_reason()
    {
        AssertFailure(
            "answered: expected the outcome 'answered' but it was 'failed (model_error)'.",
            EvalFiles.Question("answered", outcome: EvalOutcomes.Failed, errorCode: "model_error", hits: OnLabel, evidence: OnLabel));
        AssertFailure(
            "answered: expected the outcome 'answered' but it was 'insufficient_evidence'.",
            EvalFiles.Question("answered", outcome: EvalOutcomes.InsufficientEvidence, hits: OnLabel, evidence: OnLabel));
        AssertFailure(
            "answered: 2 citation(s) did not pass validation; every citation must be valid.",
            EvalFiles.Question("answered", hits: OnLabel, evidence: OnLabel, rejectedCitations: 2));
        AssertFailure(
            "answered: only 0 of 1 labeled location(s) are among the 10 best hits of the search.",
            EvalFiles.Question("answered", hits: Elsewhere, evidence: OnLabel));
        AssertFailure(
            "answered: retrieval was not measured (invalid_argument).",
            EvalFiles.Question("answered", retrievalError: "invalid_argument", evidence: OnLabel));
        AssertFailure(
            "answered: retrieval was not measured.",
            EvalFiles.Question("answered", evidence: OnLabel));
        AssertFailure(
            "answered: no validated evidence overlaps a labeled location.",
            EvalFiles.Question("answered", hits: OnLabel, evidence: Elsewhere));
        AssertFailure(
            "answered: an expectation about labeled locations was set, but the question has none.",
            EvalFiles.Question("answered", hits: OnLabel, evidence: OnLabel) with { Expected = [] });

        // A question with no expected outcome must still reach an end of its own without failing.
        AssertFailure(
            "unanswerable: expected an answer or an abstention but the outcome was 'failed (timed_out)'.",
            unanswerable: EvalFiles.Question("unanswerable", answerable: false, outcome: EvalOutcomes.Failed, errorCode: "timed_out"));
        AssertFailure(
            "unanswerable: expected an answer or an abstention but the outcome was 'not_run'.",
            unanswerable: EvalFiles.Question("unanswerable", answerable: false, outcome: EvalOutcomes.NotRun));

        static void AssertFailure(string expected, EvalQuestionResult? answered = null, EvalQuestionResult? unanswerable = null)
        {
            var passing = PassingRun();
            var run = EvalFiles.Run(answered ?? passing.Questions[0], unanswerable ?? passing.Questions[1]);
            Assert.Equal([expected], Strict.Check(run));
        }
    }

    [Fact]
    public void A_missing_question_a_stopped_run_another_dataset_and_a_revision_mismatch_all_fail()
    {
        var passing = PassingRun();

        Assert.Equal(
            ["unanswerable: the question is not in the result file."],
            Strict.Check(EvalFiles.Run(passing.Questions[0])));

        Assert.Equal(
            ["The run did not complete: its status is 'cancelled'."],
            Strict.Check(passing with { Status = EvalRunStatus.Cancelled }));

        Assert.Equal(
            ["The expectations are for dataset 'sample-repo' but the result file is a run of 'other-repo'."],
            Strict.Check(passing with { Dataset = passing.Dataset with { Name = "other-repo" } }));

        Assert.Equal(
            ["The source was not the revision the dataset was labeled against, so the labels may point at the wrong lines."],
            Strict.Check(passing with { Dataset = passing.Dataset with { RevisionMatchesSnapshot = false } }));

        // Every failure is listed, not only the first.
        var allWrong = EvalFiles.Run(
            EvalFiles.Question("answered", outcome: EvalOutcomes.InsufficientEvidence, hits: Elsewhere, rejectedCitations: 1));
        Assert.Equal(5, Strict.Check(allWrong).Count);
    }

    [Fact]
    public void Expectations_may_not_name_a_held_out_question()
    {
        // Against a result file: the question's split is recorded in it.
        var heldOut = EvalFiles.Run(
            EvalFiles.Question("answered", split: "test", hits: OnLabel, evidence: OnLabel),
            PassingRun().Questions[1]);
        var failure = Assert.Single(Strict.Check(heldOut));
        Assert.StartsWith("answered: Question 'answered' is in the held-out 'test' split.", failure, StringComparison.Ordinal);

        // Against a dataset, before a run asks anything.
        var dataset = new EvalDataset(
            "sample-repo",
            "1",
            Revision: null,
            [
                new EvalQuestion("answered", EvalDataset.TestSplit, "q1", [new EvalRange("src/A.cs", 10, 20)], Answerable: true),
                new EvalQuestion("unanswerable", EvalDataset.DevSplit, "q2", [], Answerable: false),
            ]);
        var exception = Assert.Throws<EvalExpectationsException>(() => Strict.SelectFrom(dataset));
        Assert.Contains("held-out 'test' split", exception.Message, StringComparison.Ordinal);

        var onlyOne = dataset with { Questions = [dataset.Questions[1]] };
        exception = Assert.Throws<EvalExpectationsException>(() => Strict.SelectFrom(onlyOne));
        Assert.Contains("name question 'answered', which dataset 'sample-repo' version 1 does not have", exception.Message, StringComparison.Ordinal);

        exception = Assert.Throws<EvalExpectationsException>(() => Strict.SelectFrom(dataset with { Name = "other-repo" }));
        Assert.Contains("for dataset 'sample-repo' but the dataset is 'other-repo'", exception.Message, StringComparison.Ordinal);

        var development = dataset with
        {
            Questions = [dataset.Questions[0] with { Split = EvalDataset.DevSplit }, dataset.Questions[1], dataset.Questions[1] with { Id = "extra" }],
        };
        Assert.Equal(["answered", "unanswerable"], Strict.SelectFrom(development).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("", "not JSON of the documented shape")]
    [InlineData("null", "it is empty")]
    [InlineData("""{ "questions": [{ "id": "a" }] }""", "\"dataset\" is required")]
    [InlineData("""{ "dataset": "d" }""", "at least one question")]
    [InlineData("""{ "dataset": "d", "questions": [] }""", "at least one question")]
    [InlineData("""{ "dataset": "d", "questions": [{ "outcome": "answered" }] }""", "questions[0] needs an \"id\"")]
    [InlineData("""{ "dataset": "d", "questions": [{ "id": "a" }, { "id": "a" }] }""", "listed more than once")]
    [InlineData("""{ "dataset": "d", "questions": [{ "id": "a", "outcome": "not_run" }] }""", "\"outcome\" must be")]
    [InlineData("""{ "dataset": "d", "questions": [{ "id": "a", "outcome": "Answered" }] }""", "\"outcome\" must be")]
    [InlineData("""{ "dataset": "d", "questions": [{ "id": "a", "citesLabels": true }] }""", "not JSON of the documented shape")]
    [InlineData("""{ "dataset": "d", "datasetVersion": "2", "questions": [{ "id": "a" }] }""", "not JSON of the documented shape")]
    public void A_malformed_expectations_file_is_refused(string json, string expectedMessage)
    {
        var exception = Assert.Throws<EvalExpectationsException>(() => EvalExpectations.Parse(json));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_check_command_exits_0_when_met_3_when_not_and_1_when_it_cannot_tell()
    {
        using var directory = new ScratchDirectory();
        var expectations = Path.Combine(directory.Path, "expectations.json");
        await File.WriteAllTextAsync(
            expectations,
            """
            {
              "dataset": "sample-repo",
              "description": "Free text.",
              "questions": [
                { "id": "answered", "outcome": "answered", "labelsRetrieved": true, "citesLabel": true, "notes": "Free text." },
                { "id": "unanswerable" }
              ]
            }
            """);
        var passing = await PassingRun().WriteAsync(Path.Combine(directory.Path, "passing"));
        var broken = await EvalFiles.Run(
                EvalFiles.Question("answered", outcome: EvalOutcomes.Failed, errorCode: "model_error"),
                PassingRun().Questions[1])
            .WriteAsync(Path.Combine(directory.Path, "broken"));
        var notJson = Path.Combine(directory.Path, "broken.json");
        await File.WriteAllTextAsync(notJson, "{ not json");

        var (exitCode, output, error) = await CheckAsync(passing, "--expectations", expectations);
        Assert.Equal((EvalCommand.ExitOk, string.Empty), (exitCode, error));
        Assert.Contains("Check passed: 2 question(s) of 'sample-repo' meet their expectations.", output, StringComparison.Ordinal);

        // The gate: a result that breaks an expectation ends the command with a non-zero exit
        // code and says which question and why.
        (exitCode, output, error) = await CheckAsync("--expectations", expectations, broken);
        Assert.Equal((3, string.Empty), (exitCode, output));
        Assert.Equal(EvalCommand.ExitExpectationsNotMet, exitCode);
        Assert.Contains("Check FAILED: 3 expectation(s) not met for the 2 question(s) of 'sample-repo'.", error, StringComparison.Ordinal);
        Assert.Contains("  - answered: expected the outcome 'answered' but it was 'failed (model_error)'.", error, StringComparison.Ordinal);
        Assert.Contains("  - answered: retrieval was not measured.", error, StringComparison.Ordinal);
        Assert.Contains("  - answered: no validated evidence overlaps a labeled location.", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", error, StringComparison.Ordinal);

        // Failures name questions by ID, never by their text.
        Assert.DoesNotContain("zebracorn", error, StringComparison.Ordinal);

        // It writes nothing: only the files this test wrote are there.
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "broken")));

        await AssertCannotTellAsync([passing], "'--expectations' is required.", usage: true);
        await AssertCannotTellAsync(["--expectations", expectations], "'check' needs exactly one result file.", usage: true);
        await AssertCannotTellAsync([passing, broken, "--expectations", expectations], "'check' needs exactly one result file.", usage: true);
        await AssertCannotTellAsync([passing, "--expectations"], "'--expectations' needs a value.", usage: true);
        await AssertCannotTellAsync([passing, "--expectations", expectations, "--strict"], "Unknown argument '--strict'.", usage: true);
        await AssertCannotTellAsync([passing, "--expectations", Path.Combine(directory.Path, "missing.json")], "Could not read the expectations file", usage: false);
        await AssertCannotTellAsync([passing, "--expectations", notJson], "is not a usable expectations file", usage: false);
        await AssertCannotTellAsync([notJson, "--expectations", expectations], "is not a usable result file", usage: false);
        await AssertCannotTellAsync([Path.Combine(directory.Path, "missing.json"), "--expectations", expectations], "Could not read the result file", usage: false);

        static async Task AssertCannotTellAsync(string[] args, string expectedError, bool usage)
        {
            var (exitCode, output, error) = await CheckAsync(args);
            Assert.Equal((EvalCommand.ExitError, string.Empty), (exitCode, output));
            Assert.Contains(expectedError, error, StringComparison.Ordinal);
            Assert.Equal(usage, error.Contains("Usage:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Run_and_check_arguments_carry_the_expectations_file()
    {
        Assert.Equal(
            new EvalOptions("d.json", "repo", null, null, AllowRevisionMismatch: false, KeepDatabase: false, ExpectationsPath: "e.json"),
            EvalCommand.ParseArguments(["run", "--dataset", "d.json", "--source", "repo", "--expectations", "e.json"]));

        // An expectations file already names its questions; a split on top would hide some of them.
        var exception = Assert.Throws<EvalUsageException>(() => EvalCommand.ParseArguments(
            ["run", "--dataset", "d.json", "--source", "repo", "--split", "dev", "--expectations", "e.json"]));
        Assert.Contains("cannot be combined", exception.Message, StringComparison.Ordinal);
        Assert.Throws<EvalUsageException>(() => EvalCommand.ParseArguments(["run", "--dataset", "d", "--source", "s", "--expectations"]));

        Assert.Equal(new EvalCheckOptions("r.json", "e.json"), EvalCheckCommand.ParseArguments(["check", "--expectations", "e.json", "r.json"]));
        Assert.Throws<EvalUsageException>(() => EvalCheckCommand.ParseArguments(["report", "r.json"]));
        Assert.Throws<EvalUsageException>(() => EvalCommand.ParseArguments(["check", "r.json", "--expectations", "e.json"]));
    }

    [Fact]
    public void A_report_of_a_subset_run_says_that_its_numbers_are_about_the_subset()
    {
        var run = PassingRun();
        var subset = run with { Dataset = run.Dataset with { Split = EvalDatasetInfo.SubsetSplit } };

        Assert.Contains(EvalMetrics.Report(subset).Notes, note => note.StartsWith("The run asked only the questions an expectations file lists", StringComparison.Ordinal));
        Assert.DoesNotContain(EvalMetrics.Report(subset).Notes, note => note.Contains("limited to one split", StringComparison.Ordinal));
        Assert.DoesNotContain(EvalMetrics.Report(run).Notes, note => note.Contains("expectations file", StringComparison.Ordinal));
    }

    /// <summary>Meets <see cref="Strict"/>: the label is retrieved and cited, and the unanswerable question was answered.</summary>
    private static EvalRunResult PassingRun() =>
        EvalFiles.Run(
            EvalFiles.Question("answered", hits: OnLabel, evidence: OnLabel),
            EvalFiles.Question("unanswerable", answerable: false, evidence: Elsewhere));

    private static async Task<(int ExitCode, string Output, string Error)> CheckAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await EvalCommand.RunAsync(
            ["check", .. args],
            output,
            error,
            _ => Assert.Fail("check must not build the pipeline's services."));
        return (exitCode, output.ToString(), error.ToString());
    }

    private static string RepositoryRoot() =>
        EvalCommand.FindRepositoryRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Could not locate Trace.slnx above the test output directory.");

    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"trace-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
