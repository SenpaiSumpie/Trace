using System.Text.Json;

namespace Trace.Application.Investigations;

/// <summary>The model's final output was not a usable answer; the investigation fails rather than guess.</summary>
public sealed class InvalidModelOutputException(string message) : Exception(message);

public sealed record ModelCitation(string? Path, int StartLine, int EndLine, string? Supports);

/// <summary>The model's final answer, parsed and bounded but not yet validated against the snapshot.</summary>
public sealed record ModelAnswer(
    bool SufficientEvidence,
    string? Answer,
    IReadOnlyList<ModelCitation>? Citations,
    IReadOnlyList<string>? Unknowns)
{
    public const int MaxAnswerLength = 8_000;
    public const int MaxCitations = 20;
    public const int MaxUnknowns = 10;
    public const int MaxShortTextLength = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ModelAnswer Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidModelOutputException("The model returned no answer.");
        }

        ModelAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize<ModelAnswer>(StripCodeFence(text), Json);
        }
        catch (JsonException)
        {
            throw new InvalidModelOutputException("The model's answer was not valid JSON of the required shape.");
        }

        if (answer is null)
        {
            throw new InvalidModelOutputException("The model's answer was empty.");
        }

        if (answer.Answer?.Length > MaxAnswerLength)
        {
            throw new InvalidModelOutputException($"The model's answer exceeded {MaxAnswerLength} characters.");
        }

        if (answer.Citations?.Count > MaxCitations || answer.Citations?.Any(citation => citation is null) == true)
        {
            throw new InvalidModelOutputException($"The model's answer must have at most {MaxCitations} well-formed citations.");
        }

        return answer;
    }

    /// <summary>Models often wrap JSON in a Markdown code fence despite being told not to.</summary>
    internal static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        return firstLineEnd < 0 || trimmed.Length < firstLineEnd + 4
            ? trimmed
            : trimmed[(firstLineEnd + 1)..^3];
    }
}
