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
    public const int VisitorKeyLength = 64;

    private Investigation()
    {
    }

    public Guid RepositoryId { get; private set; }

    public Guid SnapshotId { get; private set; }

    public InvestigationType Type { get; private set; }

    public string Question { get; private set; } = null!;

    public InvestigationState State { get; private set; }

    /// <summary>Who asked; null for anonymous use of the public demo repository.</summary>
    public Guid? UserId { get; private set; }

    /// <summary>
    /// For an anonymous request: the lowercase hex SHA-256 of the random token in the asking
    /// browser's visitor cookie, so that browser, and nobody else, can list what it asked. Null
    /// for a signed-in user and for rows from before history existed. Never the token itself.
    /// </summary>
    public string? VisitorKey { get; private set; }

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

    /// <summary>Model tokens spent so far, across every attempt.</summary>
    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    /// <summary>The part of <see cref="InputTokens"/> that was read from the provider's prompt cache.</summary>
    public long CacheReadTokens { get; private set; }

    /// <summary>The part of <see cref="InputTokens"/> that was written to the provider's prompt cache.</summary>
    public long CacheWriteTokens { get; private set; }

    public static Investigation Create(
        IndexSnapshot snapshot,
        InvestigationType type,
        string question,
        string? idempotencyKey = null,
        Guid? userId = null,
        string? visitorKey = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != SnapshotStatus.Completed)
        {
            throw new InvalidOperationException("An investigation can only pin a completed snapshot.");
        }

        if (visitorKey is not null && (userId is not null || !IsVisitorKey(visitorKey)))
        {
            throw new ArgumentException(
                "A visitor key is 64 lowercase hex characters and is only for an investigation without a user.",
                nameof(visitorKey));
        }

        return new Investigation
        {
            RepositoryId = snapshot.RepositoryId,
            SnapshotId = snapshot.Id,
            Type = type,
            Question = Guard.Required(question, MaxQuestionLength, nameof(question)),
            State = InvestigationState.Queued,
            UserId = userId,
            VisitorKey = visitorKey,
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

    public void RecordUsage(long inputTokens, long outputTokens, long cacheReadTokens = 0, long cacheWriteTokens = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheReadTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheWriteTokens);
        InputTokens += inputTokens;
        OutputTokens += outputTokens;
        CacheReadTokens += cacheReadTokens;
        CacheWriteTokens += cacheWriteTokens;
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

    /// <summary>Whether the text has the shape of a visitor key: a lowercase hex SHA-256.</summary>
    public static bool IsVisitorKey(string? value) =>
        value is { Length: VisitorKeyLength } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

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
