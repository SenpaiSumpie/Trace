namespace Trace.Domain;

public enum InvestigationEventType
{
    Started,

    /// <summary>An allowed tool ran and returned a result.</summary>
    ToolCalled,

    /// <summary>An allowed tool was called but returned an error (bad arguments, unknown path, ...).</summary>
    ToolFailed,

    /// <summary>A tool request was refused without running anything (unknown tool, budget exhausted).</summary>
    ToolRejected,

    /// <summary>A citation in the model's answer did not survive validation and was discarded.</summary>
    CitationRejected,

    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One user-visible step of an investigation: what was done, never the model's private reasoning.
/// Events are append-only and ordered by <see cref="Seq"/>.
/// </summary>
public sealed class InvestigationEvent : Entity
{
    public const int MaxSummaryLength = 500;
    public const int MaxToolNameLength = 100;

    private InvestigationEvent()
    {
    }

    public Guid InvestigationId { get; private set; }

    /// <summary>1-based position within the investigation.</summary>
    public int Seq { get; private set; }

    public InvestigationEventType EventType { get; private set; }

    public string? ToolName { get; private set; }

    public string Summary { get; private set; } = null!;

    /// <summary>Small machine-readable details (JSON), e.g. latency and result count. Never source text or secrets.</summary>
    public string? MetadataJson { get; private set; }

    public static InvestigationEvent Create(
        Guid investigationId,
        int seq,
        InvestigationEventType eventType,
        string summary,
        string? toolName = null,
        string? metadataJson = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seq, 1);

        return new InvestigationEvent
        {
            InvestigationId = investigationId,
            Seq = seq,
            EventType = eventType,
            Summary = Guard.Required(summary, MaxSummaryLength, nameof(summary)),
            ToolName = toolName is null ? null : Guard.Required(toolName, MaxToolNameLength, nameof(toolName)),
            MetadataJson = metadataJson,
        };
    }
}
