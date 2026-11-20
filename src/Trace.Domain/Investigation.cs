namespace Trace.Domain;

public enum InvestigationType
{
    Question,
    IssueToPlan,
}

public enum InvestigationState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A question or issue investigation, always pinned to one completed snapshot.</summary>
public sealed class Investigation : Entity
{
    public const int MaxQuestionLength = 4000;

    private Investigation()
    {
    }

    public Guid RepositoryId { get; private set; }

    public Guid SnapshotId { get; private set; }

    public InvestigationType Type { get; private set; }

    public string Question { get; private set; } = null!;

    public InvestigationState State { get; private set; }

    /// <summary>Validated structured result (JSON); null until the investigation completes.</summary>
    public string? ResultJson { get; private set; }

    public static Investigation Create(IndexSnapshot snapshot, InvestigationType type, string question)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != SnapshotStatus.Completed)
        {
            throw new InvalidOperationException("An investigation can only pin a completed snapshot.");
        }

        return new Investigation
        {
            RepositoryId = snapshot.RepositoryId,
            SnapshotId = snapshot.Id,
            Type = type,
            Question = Guard.Required(question, MaxQuestionLength, nameof(question)),
            State = InvestigationState.Queued,
        };
    }
}
