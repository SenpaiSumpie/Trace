namespace Trace.Domain;

/// <summary>
/// The implementation plan of one issue-to-plan investigation, and the issue it was made from.
/// <para>
/// A plan is created together with its investigation, when only the issue number is known. It
/// is <em>unfinished</em> until the investigation completes and gives it a result; from then on
/// nothing about it can change except that it can be marked as reviewed, once. The database
/// enforces the same rules (ADR-016).
/// </para>
/// <para>
/// Marking a plan as reviewed is an acknowledgement inside TRACE. It changes nothing else and
/// nothing on GitHub.
/// </para>
/// </summary>
public sealed class Plan : Entity
{
    /// <summary>The same limit issue titles are cut to when they are read (<c>IssueLimits.MaxTitleLength</c>).</summary>
    public const int MaxIssueTitleLength = 256;

    public const int MaxSchemaVersionLength = 20;

    /// <summary>Upper bound on the stored result, in characters of JSON.</summary>
    public const int MaxResultLength = 2_000_000;

    private Plan()
    {
    }

    public Guid InvestigationId { get; private set; }

    /// <summary>Always the investigation's pinned snapshot.</summary>
    public Guid SnapshotId { get; private set; }

    /// <summary>The repository the issue belongs to; always the investigation's repository.</summary>
    public Guid RepositoryId { get; private set; }

    public int IssueNumber { get; private set; }

    /// <summary>
    /// The issue's title as it was when the plan's run read it: one line, cleaned and cut. Null
    /// until the issue has been read. Written by whoever can open an issue: data, never markup
    /// or instructions.
    /// </summary>
    public string? IssueTitle { get; private set; }

    /// <summary>
    /// Lowercase hex SHA-256 over the issue text the plan was made from. Null until the issue
    /// has been read. The text itself is not stored.
    /// </summary>
    public string? IssueContentHash { get; private set; }

    /// <summary>Version of the schema <see cref="ResultJson"/> follows; null until the plan is finished.</summary>
    public string? SchemaVersion { get; private set; }

    /// <summary>The validated plan (JSON); null until the plan is finished.</summary>
    public string? ResultJson { get; private set; }

    /// <summary>UTC. When a person first acknowledged having reviewed the plan; null until then.</summary>
    public DateTimeOffset? ReviewedAt { get; private set; }

    public bool IsFinished => ResultJson is not null;

    public bool IsReviewed => ReviewedAt is not null;

    public static Plan Create(Investigation investigation, int issueNumber)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        if (investigation.Type != InvestigationType.IssueToPlan)
        {
            throw new InvalidOperationException("Only an issue-to-plan investigation has a plan.");
        }

        if (investigation.IsFinished)
        {
            throw new InvalidOperationException($"Investigation is already {investigation.State}; a plan cannot be added to it.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(issueNumber, 1);

        return new Plan
        {
            InvestigationId = investigation.Id,
            SnapshotId = investigation.SnapshotId,
            RepositoryId = investigation.RepositoryId,
            IssueNumber = issueNumber,
        };
    }

    /// <summary>
    /// Records the issue as the run read it. May be called again while the plan is unfinished: a
    /// retried run reads the issue again, and the last reading is the one the plan is made from.
    /// </summary>
    /// <param name="title">One line of at most <see cref="MaxIssueTitleLength"/> characters; may be empty.</param>
    /// <param name="contentHash">Lowercase hex SHA-256 of the issue text.</param>
    public void RecordIssue(string title, string contentHash)
    {
        RequireUnfinished();
        ArgumentNullException.ThrowIfNull(title);
        if (title.Length > MaxIssueTitleLength)
        {
            throw new ArgumentException($"Must be at most {MaxIssueTitleLength} characters.", nameof(title));
        }

        if (title.Any(char.IsControl))
        {
            throw new ArgumentException("Must be a single line without control characters.", nameof(title));
        }

        ArgumentNullException.ThrowIfNull(contentHash);
        if (contentHash.Length != 64 || contentHash.Any(character => character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))))
        {
            throw new ArgumentException("Must be a lowercase hex SHA-256.", nameof(contentHash));
        }

        IssueTitle = title;
        IssueContentHash = contentHash;
    }

    /// <summary>
    /// Gives the plan its result. Only possible once: its investigation must have completed and
    /// the issue it was made from must have been recorded.
    /// </summary>
    public void Complete(Investigation investigation, string schemaVersion, string resultJson)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        if (investigation.Id != InvestigationId)
        {
            throw new InvalidOperationException("A plan can only be completed by its own investigation.");
        }

        RequireUnfinished();
        if (investigation.State != InvestigationState.Completed)
        {
            throw new InvalidOperationException($"Investigation is {investigation.State}; its plan cannot be completed.");
        }

        if (IssueTitle is null || IssueContentHash is null)
        {
            throw new InvalidOperationException("The issue the plan was made from has not been recorded.");
        }

        Guard.Required(schemaVersion, MaxSchemaVersionLength, nameof(schemaVersion));
        Guard.Required(resultJson, MaxResultLength, nameof(resultJson));
        SchemaVersion = schemaVersion;
        ResultJson = resultJson;
    }

    /// <summary>
    /// Acknowledges that a person has reviewed the finished plan. Marking twice is not an error:
    /// the first time stands and nothing changes.
    /// </summary>
    /// <returns>True if this call marked the plan; false if it already was reviewed.</returns>
    /// <exception cref="InvalidOperationException">The plan is unfinished: there is nothing to review.</exception>
    public bool MarkReviewed(DateTimeOffset now)
    {
        if (!IsFinished)
        {
            throw new InvalidOperationException("An unfinished plan cannot be reviewed.");
        }

        if (IsReviewed)
        {
            return false;
        }

        ReviewedAt = now.ToUniversalTime();
        return true;
    }

    private void RequireUnfinished()
    {
        if (IsFinished)
        {
            throw new InvalidOperationException("The plan is finished and cannot be changed.");
        }
    }
}
