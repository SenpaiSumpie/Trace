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
    public const int MaxIdempotencyKeyLength = 100;
    public const int MaxVersionLength = 100;
    public const int MaxErrorCodeLength = 100;

    private Investigation()
    {
    }

    public Guid RepositoryId { get; private set; }

    public Guid SnapshotId { get; private set; }

    public InvestigationType Type { get; private set; }

    public string Question { get; private set; } = null!;

    public InvestigationState State { get; private set; }

    /// <summary>Client-supplied key; a repeated request with the same key returns this investigation.</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>The chat model that ran the investigation; set when it starts.</summary>
    public string? ModelId { get; private set; }

    /// <summary>Version of the prompt and tool contract used; set when it starts.</summary>
    public string? PromptVersion { get; private set; }

    /// <summary>Validated structured result (JSON); null until the investigation completes.</summary>
    public string? ResultJson { get; private set; }

    /// <summary>Stable machine-readable reason; set only when the investigation failed.</summary>
    public string? ErrorCode { get; private set; }

    public static Investigation Create(
        IndexSnapshot snapshot,
        InvestigationType type,
        string question,
        string? idempotencyKey = null)
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
            IdempotencyKey = idempotencyKey is null
                ? null
                : Guard.Required(idempotencyKey, MaxIdempotencyKeyLength, nameof(idempotencyKey)),
        };
    }

    /// <summary>
    /// Begins, or after an interrupted attempt resumes, the investigation. Resuming is allowed
    /// because a worker can die mid-run and the job is then retried from the start.
    /// </summary>
    public void Start(string modelId, string promptVersion)
    {
        if (State is not (InvestigationState.Queued or InvestigationState.Running))
        {
            throw new InvalidOperationException($"Investigation is {State}; it cannot be started.");
        }

        ModelId = Guard.Required(modelId, MaxVersionLength, nameof(modelId));
        PromptVersion = Guard.Required(promptVersion, MaxVersionLength, nameof(promptVersion));
        State = InvestigationState.Running;
    }

    public void Complete(string resultJson)
    {
        Require(InvestigationState.Running);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        ResultJson = resultJson;
        State = InvestigationState.Completed;
    }

    public void Fail(string errorCode)
    {
        RequireUnfinished();
        ErrorCode = Guard.Required(errorCode, MaxErrorCodeLength, nameof(errorCode));
        State = InvestigationState.Failed;
    }

    public void Cancel()
    {
        RequireUnfinished();
        State = InvestigationState.Cancelled;
    }

    public bool IsFinished =>
        State is InvestigationState.Completed or InvestigationState.Failed or InvestigationState.Cancelled;

    private void RequireUnfinished()
    {
        if (IsFinished)
        {
            throw new InvalidOperationException($"Investigation is already {State}.");
        }
    }

    private void Require(InvestigationState expected)
    {
        if (State != expected)
        {
            throw new InvalidOperationException($"Investigation is {State}; expected {expected}.");
        }
    }
}
