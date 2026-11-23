using Trace.Evals;

namespace Trace.UnitTests;

public class EvalDatasetTests
{
    private const string Revision = "ee02b2da366aa000b30541463014b895d5cebb67";

    [Fact]
    public void The_checked_in_fixture_questions_are_a_valid_dataset()
    {
        var dataset = EvalDataset.Load(Path.Combine(RepositoryRoot(), "tests", "fixtures", "sample-repo.questions.json"));

        Assert.Equal(("sample-repo", "1", Revision), (dataset.Name, dataset.Version, dataset.Revision));
        Assert.Equal(11, dataset.Questions.Count);
        Assert.All(dataset.Questions, question =>
        {
            Assert.True(question.Answerable);
            Assert.NotEmpty(question.Expected);
            Assert.Contains(question.Split, new[] { EvalDataset.DevSplit, EvalDataset.TestSplit });
        });

        var first = dataset.Questions[0];
        Assert.Equal(("pagination-offset", "dev"), (first.Id, first.Split));
        Assert.Equal(new EvalRange("src/TaskBoard.Api/Tasks/TaskRepository.cs", 20, 29), Assert.Single(first.Expected));
    }

    [Fact]
    public void Extensions_are_read_and_the_name_defaults_to_the_file_name()
    {
        var dataset = EvalDataset.Parse(
            $$"""
            {
              "datasetVersion": "2026-11",
              "description": "free text",
              "revision": "{{Revision}}",
              "questions": [
                { "id": "has-answer", "split": "dev", "question": "Where is it?", "notes": "why this label",
                  "expected": [{ "path": "src/A.cs", "startLine": 3, "endLine": 3 }] },
                { "id": "no-answer", "split": "test", "question": "Which cloud region is it deployed to?", "answerable": false },
                { "id": "no-answer-empty-list", "split": "test", "question": "Who is on call?", "answerable": false, "expected": [] }
              ]
            }
            """,
            fallbackName: "my-repo");

        Assert.Equal(("my-repo", "2026-11", Revision), (dataset.Name, dataset.Version, dataset.Revision));
        Assert.Equal([true, false, false], dataset.Questions.Select(question => question.Answerable));
        Assert.Empty(dataset.Questions[1].Expected);
    }

    [Fact]
    public void The_older_revision_name_is_accepted_and_no_revision_is_allowed()
    {
        Assert.Equal(Revision, EvalDataset.Parse(Dataset(header: $"\"fixtureRevision\": \"{Revision}\","), "d").Revision);
        Assert.Equal(Revision, EvalDataset.Parse(
            Dataset(header: $"\"fixtureRevision\": \"{Revision}\", \"revision\": \"{Revision}\","), "d").Revision);
        Assert.Null(EvalDataset.Parse(Dataset(), "d").Revision);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("null", "empty")]
    [InlineData("""{ "datasetVersion": "1", "questions": [] }""", "at least one question")]
    [InlineData("""{ "questions": [{ "id": "a", "split": "dev", "question": "q", "expected": [{ "path": "a", "startLine": 1, "endLine": 1 }] }] }""", "datasetVersion")]
    [InlineData("""{ "datasetVersion": "1", "questons": [] }""", "not valid JSON")]
    public void A_malformed_file_is_refused(string json, string expectedMessage)
    {
        var exception = Assert.Throws<EvalDatasetException>(() => EvalDataset.Parse(json, "d"));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "id": "a", "split": "train", "question": "q", "expected": [{ "path": "a", "startLine": 1, "endLine": 1 }] }""", "split")]
    [InlineData("""{ "id": "Bad Id", "split": "dev", "question": "q", "expected": [{ "path": "a", "startLine": 1, "endLine": 1 }] }""", "id must be")]
    [InlineData("""{ "split": "dev", "question": "q", "expected": [{ "path": "a", "startLine": 1, "endLine": 1 }] }""", "id must be")]
    [InlineData("""{ "id": "a", "split": "dev", "question": " ", "expected": [{ "path": "a", "startLine": 1, "endLine": 1 }] }""", "\"question\" is required")]
    [InlineData("""{ "id": "a", "split": "dev", "question": "q" }""", "at least one \"expected\"")]
    [InlineData("""{ "id": "a", "split": "dev", "question": "q", "expected": [] }""", "at least one \"expected\"")]
    [InlineData("""{ "id": "a", "split": "dev", "question": "q", "answerable": false, "expected": [{ "path": "a", "startLine": 1, "endLine": 1 }] }""", "must not list")]
    [InlineData("""{ "id": "a", "split": "dev", "question": "q", "expected": [{ "path": "a", "startLine": 0, "endLine": 1 }] }""", "startLine")]
    [InlineData("""{ "id": "a", "split": "dev", "question": "q", "expected": [{ "path": "a", "startLine": 5, "endLine": 4 }] }""", "startLine")]
    [InlineData("""{ "id": "a", "split": "dev", "question": "q", "expected": [{ "path": "a", "startLine": 5 }] }""", "startLine")]
    [InlineData("""{ "id": "a", "split": "dev", "question": "q", "expected": [{ "path": "src\\A.cs", "startLine": 1, "endLine": 1 }] }""", "forward slashes")]
    [InlineData("""{ "id": "a", "split": "dev", "question": "q", "expected": [{ "path": "/etc/passwd", "startLine": 1, "endLine": 1 }] }""", "repository-relative")]
    [InlineData("""{ "id": "a", "split": "dev", "question": "q", "expectd": [{ "path": "a", "startLine": 1, "endLine": 1 }] }""", "not valid JSON")]
    public void A_question_that_breaks_a_rule_is_refused(string question, string expectedMessage)
    {
        var exception = Assert.Throws<EvalDatasetException>(() => EvalDataset.Parse(Dataset(questions: question), "d"));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_ids_conflicting_revisions_bad_names_and_overlong_questions_are_refused()
    {
        const string Question = """{ "id": "a", "split": "dev", "question": "q", "expected": [{ "path": "a", "startLine": 1, "endLine": 1 }] }""";

        Assert.Contains(
            "more than once",
            Assert.Throws<EvalDatasetException>(() => EvalDataset.Parse(Dataset(questions: $"{Question}, {Question}"), "d")).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "differ",
            Assert.Throws<EvalDatasetException>(() => EvalDataset.Parse(
                Dataset(header: $"\"revision\": \"{Revision}\", \"fixtureRevision\": \"{new string('a', 40)}\","), "d")).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "40-character",
            Assert.Throws<EvalDatasetException>(() => EvalDataset.Parse(Dataset(header: "\"revision\": \"main\","), "d")).Message,
            StringComparison.Ordinal);

        // The name becomes part of the result file's name, so it may not carry a path.
        Assert.Throws<EvalDatasetException>(() => EvalDataset.Parse(Dataset(header: "\"name\": \"../escape\","), "d"));
        Assert.Throws<EvalDatasetException>(() => EvalDataset.Parse(Dataset(), fallbackName: "Has Spaces"));

        var tooLong = Question.Replace("\"q\"", $"\"{new string('q', EvalDataset.MaxQuestionLength + 1)}\"", StringComparison.Ordinal);
        Assert.Throws<EvalDatasetException>(() => EvalDataset.Parse(Dataset(questions: tooLong), "d"));
    }

    [Fact]
    public void A_missing_file_is_reported_as_a_dataset_error()
    {
        var exception = Assert.Throws<EvalDatasetException>(
            () => EvalDataset.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.questions.json")));

        Assert.Contains("Could not read", exception.Message, StringComparison.Ordinal);
    }

    private static string Dataset(
        string header = "",
        string questions = """{ "id": "a", "split": "dev", "question": "q", "expected": [{ "path": "a", "startLine": 1, "endLine": 1 }] }""") =>
        $$"""{ {{header}} "datasetVersion": "1", "questions": [{{questions}}] }""";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trace.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Trace.slnx above the test output directory.");
    }
}
