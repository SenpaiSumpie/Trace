using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trace.Application.Investigations;

public enum InvestigationOutcome
{
    /// <summary>The answer is backed by at least one validated citation.</summary>
    Answered,

    /// <summary>No answer could be grounded in the snapshot; no unverified answer text is kept.</summary>
    InsufficientEvidence,
}

/// <summary>A validated citation: the location is real and the excerpt was read from the snapshot.</summary>
public sealed record EvidenceItem(string Path, int StartLine, int EndLine, string Supports, string Excerpt);

/// <summary>
/// The structured, validated result stored on a completed investigation. Nothing in it is
/// passed through from the model unchecked: citations are validated and text is length-bounded.
/// </summary>
public sealed record InvestigationResult(
    int SchemaVersion,
    InvestigationOutcome Outcome,
    string Answer,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> Limitations)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static InvestigationResult FromJson(string json) =>
        JsonSerializer.Deserialize<InvestigationResult>(json, Json)
        ?? throw new JsonException("Stored investigation result is null.");
}
