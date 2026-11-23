using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Trace.Evals;

/// <summary>The dataset file is missing, malformed or breaks one of the format's rules.</summary>
public sealed class EvalDatasetException(string message) : Exception(message);

/// <summary>A labeled source location: lines <c>StartLine..EndLine</c> (inclusive, 1-based) of one file.</summary>
public sealed record EvalRange(string Path, int StartLine, int EndLine);

/// <param name="Answerable">
/// False for a question the repository cannot answer: the correct result is "not enough
/// evidence", and there are no expected locations.
/// </param>
public sealed record EvalQuestion(
    string Id,
    string Split,
    string Question,
    IReadOnlyList<EvalRange> Expected,
    bool Answerable);

/// <summary>
/// A set of labeled questions about one repository revision. The format is documented in
/// <c>docs/evaluation.md</c>; it extends <c>tests/fixtures/sample-repo.questions.json</c>.
/// </summary>
/// <param name="Revision">The revision the labels were written against, if the file states one.</param>
public sealed partial record EvalDataset(
    string Name,
    string Version,
    string? Revision,
    IReadOnlyList<EvalQuestion> Questions)
{
    public const string DevSplit = "dev";
    public const string TestSplit = "test";
    public const string FileSuffix = ".questions.json";
    public const int MaxVersionLength = 50;

    /// <summary>Longest question the investigation pipeline accepts.</summary>
    public const int MaxQuestionLength = Domain.Investigation.MaxQuestionLength;

    // A typo in a label file should fail loudly, not silently drop a label.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static EvalDataset Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new EvalDatasetException($"Could not read the dataset file '{path}': {exception.Message}");
        }

        var fileName = System.IO.Path.GetFileName(path);
        var fallbackName = fileName.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^FileSuffix.Length]
            : System.IO.Path.GetFileNameWithoutExtension(fileName);
        return Parse(json, fallbackName);
    }

    /// <param name="fallbackName">Used as the dataset name when the file has no <c>name</c>.</param>
    public static EvalDataset Parse(string json, string fallbackName)
    {
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, Json);
        }
        catch (JsonException exception)
        {
            throw new EvalDatasetException($"The dataset is not valid JSON of the documented shape: {exception.Message}");
        }

        if (file is null)
        {
            throw new EvalDatasetException("The dataset is empty.");
        }

        var name = file.Name ?? fallbackName;
        if (!NamePattern().IsMatch(name))
        {
            throw new EvalDatasetException(
                $"Dataset name '{name}' must be 1-64 characters of a-z, 0-9, '.', '_' or '-', starting with a letter or digit. Set \"name\" in the file.");
        }

        if (string.IsNullOrWhiteSpace(file.DatasetVersion) || file.DatasetVersion.Length > MaxVersionLength)
        {
            throw new EvalDatasetException($"\"datasetVersion\" is required and must be at most {MaxVersionLength} characters.");
        }

        if (file.Revision is not null && file.FixtureRevision is not null && file.Revision != file.FixtureRevision)
        {
            throw new EvalDatasetException("\"revision\" and its older name \"fixtureRevision\" are both set and differ.");
        }

        var revision = file.Revision ?? file.FixtureRevision;
        if (revision is not null && !RevisionPattern().IsMatch(revision))
        {
            throw new EvalDatasetException("\"revision\" must be a full 40-character lowercase hexadecimal revision ID.");
        }

        if (file.Questions is not { Count: > 0 })
        {
            throw new EvalDatasetException("\"questions\" must contain at least one question.");
        }

        var questions = new List<EvalQuestion>(file.Questions.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < file.Questions.Count; index++)
        {
            var question = ParseQuestion(file.Questions[index], index);
            if (!ids.Add(question.Id))
            {
                throw new EvalDatasetException($"Question id '{question.Id}' is used more than once.");
            }

            questions.Add(question);
        }

        return new EvalDataset(name, file.DatasetVersion, revision, questions);
    }

    private static EvalQuestion ParseQuestion(QuestionDto? dto, int index)
    {
        if (dto is null)
        {
            throw new EvalDatasetException($"questions[{index}] is null.");
        }

        if (dto.Id is null || !IdPattern().IsMatch(dto.Id))
        {
            throw new EvalDatasetException(
                $"questions[{index}].id must be 1-100 characters of a-z, 0-9, '.', '_' or '-', starting with a letter or digit.");
        }

        var where = $"Question '{dto.Id}'";
        if (dto.Split is not (DevSplit or TestSplit))
        {
            throw new EvalDatasetException($"{where}: \"split\" must be \"{DevSplit}\" or \"{TestSplit}\".");
        }

        if (string.IsNullOrWhiteSpace(dto.Question) || dto.Question.Length > MaxQuestionLength)
        {
            throw new EvalDatasetException($"{where}: \"question\" is required and must be at most {MaxQuestionLength} characters.");
        }

        var expected = new List<EvalRange>();
        foreach (var range in dto.Expected ?? [])
        {
            if (range is null
                || string.IsNullOrWhiteSpace(range.Path)
                || range.Path.StartsWith('/')
                || range.Path.Contains('\\', StringComparison.Ordinal)
                || range.StartLine is not >= 1
                || range.EndLine is not { } endLine
                || endLine < range.StartLine)
            {
                throw new EvalDatasetException(
                    $"{where}: every \"expected\" entry needs a repository-relative \"path\" with forward slashes and 1 <= \"startLine\" <= \"endLine\".");
            }

            expected.Add(new EvalRange(range.Path, range.StartLine.Value, endLine));
        }

        var answerable = dto.Answerable ?? true;
        if (answerable && expected.Count == 0)
        {
            throw new EvalDatasetException(
                $"{where}: an answerable question needs at least one \"expected\" location. Set \"answerable\": false if the repository cannot answer it.");
        }

        if (!answerable && expected.Count > 0)
        {
            throw new EvalDatasetException($"{where}: a question with \"answerable\": false must not list \"expected\" locations.");
        }

        return new EvalQuestion(dto.Id, dto.Split, dto.Question, expected, answerable);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[0-9a-f]{40}\z")]
    private static partial Regex RevisionPattern();

    private sealed record FileDto(
        string? Name,
        string? DatasetVersion,
        string? Description,
        string? Revision,
        string? FixtureRevision,
        List<QuestionDto?>? Questions);

    private sealed record QuestionDto(
        string? Id,
        string? Split,
        string? Question,
        List<RangeDto?>? Expected,
        bool? Answerable,
        string? Notes);

    private sealed record RangeDto(string? Path, int? StartLine, int? EndLine);
}
