namespace Trace.Contracts.V1;

public sealed record SnapshotResponse(Guid Id, string CommitSha, string Branch, DateTimeOffset CompletedAt);

public sealed record RepositoryResponse(
    Guid Id,
    string Owner,
    string Name,
    string DefaultBranch,
    SnapshotResponse? LatestSnapshot);

/// <summary>Body of <c>POST /api/v1/investigations</c>.</summary>
/// <param name="Type">Only <c>question</c> is supported so far.</param>
/// <param name="IdempotencyKey">Optional. Repeating a request with the same key returns the first investigation.</param>
public sealed record CreateInvestigationRequest(
    Guid RepositoryId,
    Guid SnapshotId,
    string? Type,
    string? Question,
    string? IdempotencyKey);

public sealed record EvidenceResponse(
    string Source,
    string Path,
    int StartLine,
    int EndLine,
    string CommitSha,
    string Supports,
    string Excerpt);

/// <param name="Outcome"><c>answered</c> or <c>insufficient_evidence</c>.</param>
public sealed record InvestigationResultResponse(
    string Outcome,
    string Answer,
    IReadOnlyList<EvidenceResponse> Evidence,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> Limitations);

/// <param name="State"><c>queued</c>, <c>running</c>, <c>completed</c>, <c>failed</c> or <c>cancelled</c>. Poll until it is one of the last three.</param>
/// <param name="JobId">The background job running the investigation; use it to cancel.</param>
/// <param name="Result">Present only when <paramref name="State"/> is <c>completed</c>.</param>
/// <param name="ErrorCode">Present only when <paramref name="State"/> is <c>failed</c>.</param>
public sealed record InvestigationResponse(
    Guid InvestigationId,
    Guid? JobId,
    Guid RepositoryId,
    Guid SnapshotId,
    string CommitSha,
    string Type,
    string Question,
    string State,
    string? ModelId,
    string? PromptVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    InvestigationResultResponse? Result,
    string? ErrorCode,
    UsageResponse Usage);

/// <summary>Model tokens spent on the investigation so far, across all attempts.</summary>
/// <param name="InputTokens">Every input token, including the two cached kinds.</param>
/// <param name="CacheReadTokens">Input tokens read from the provider's prompt cache (billed at a fraction of the normal price).</param>
/// <param name="CacheWriteTokens">Input tokens written to the provider's prompt cache (billed somewhat above the normal price).</param>
public sealed record UsageResponse(long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0);

public sealed record InvestigationEventResponse(
    int Seq,
    string EventType,
    string? ToolName,
    string Summary,
    DateTimeOffset OccurredAt);

/// <param name="Kind"><c>investigation.run</c> or <c>repository.index</c>.</param>
/// <param name="Status"><c>queued</c>, <c>running</c>, <c>succeeded</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="ErrorCode">Why the job failed, or the reason for its most recent retry.</param>
public sealed record JobResponse(
    Guid Id,
    string Kind,
    string Status,
    int Attempts,
    int MaxAttempts,
    bool CancelRequested,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>An issue as it appears in a list: no body. Every text field is untrusted content.</summary>
/// <param name="State"><c>open</c> or <c>closed</c>.</param>
/// <param name="Author">The login of whoever opened it, when known.</param>
public sealed record IssueSummaryResponse(
    int Number,
    string Title,
    string State,
    string? Author,
    IReadOnlyList<string> Labels,
    int CommentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One page of a list. Pass <paramref name="NextCursor"/> back as <c>cursor</c> to get the
/// next page; null means there is none. A page may hold fewer items than the limit asked for.
/// </summary>
public sealed record IssueListResponse(IReadOnlyList<IssueSummaryResponse> Items, string? NextCursor);

/// <param name="BodyTruncated">The comment was longer than the limit and has been cut.</param>
public sealed record IssueCommentResponse(long Id, string? Author, string Body, bool BodyTruncated, DateTimeOffset CreatedAt);

/// <summary>One issue with its first comments, oldest first. Every text field is untrusted content.</summary>
/// <param name="BodyTruncated">The body was longer than the limit and has been cut.</param>
/// <param name="CommentCount">How many comments the issue has, which can be more than are returned.</param>
/// <param name="CommentsTruncated">True when the issue has comments that are not in <paramref name="Comments"/>.</param>
public sealed record IssueResponse(
    int Number,
    string Title,
    string State,
    string? Author,
    IReadOnlyList<string> Labels,
    string Body,
    bool BodyTruncated,
    int CommentCount,
    IReadOnlyList<IssueCommentResponse> Comments,
    bool CommentsTruncated,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserResponse(string Login);

/// <param name="User">The signed-in user, or null when anonymous.</param>
/// <param name="SignInAvailable">False when no GitHub App is configured.</param>
/// <param name="CanStartWork">Whether this caller may ask questions and queue jobs.</param>
public sealed record MeResponse(UserResponse? User, bool SignInAvailable, bool CanStartWork);

/// <summary>Body of <c>POST /api/v1/repositories/{id}/issues/{number}/plans</c>.</summary>
/// <param name="SnapshotId">The completed snapshot the plan is pinned to.</param>
/// <param name="IdempotencyKey">Optional. Repeating a request with the same key returns the first plan.</param>
public sealed record CreatePlanRequest(Guid SnapshotId, string? IdempotencyKey);

/// <summary>The issue a plan was made from, as the plan's run read it. Untrusted content.</summary>
/// <param name="Title">One line, cut to 256 characters; null until the run has read the issue.</param>
public sealed record PlanIssueResponse(int Number, string? Title);

/// <summary>A sentence of a plan and the evidence it rests on. The text is the model's: untrusted content.</summary>
/// <param name="Citations">IDs of entries in <see cref="PlanDocumentResponse.Evidence"/>. Empty means the server verified nothing for it.</param>
public sealed record PlanStatementResponse(string Text, IReadOnlyList<Guid> Citations);

/// <param name="Change"><c>modify</c>, <c>create</c> or <c>delete</c>.</param>
/// <param name="Citations">IDs of entries in <see cref="PlanDocumentResponse.Evidence"/>; a new file may have none.</param>
public sealed record PlanFileResponse(string Path, string Change, string Reason, IReadOnlyList<Guid> Citations);

/// <summary>One verified source location with the text that is there, read from the snapshot.</summary>
/// <param name="Source">Always <c>code</c>.</param>
public sealed record PlanEvidenceResponse(
    Guid Id,
    string Source,
    string Path,
    int StartLine,
    int EndLine,
    string CommitSha,
    string Excerpt);

/// <summary>
/// The stored plan document (<c>plan-v1</c>). Steps, risks, verification entries and modified or
/// deleted files each cite evidence the server verified. Unknowns, the reason of a new file and
/// a summary without citations are the model's unsupported statements.
/// </summary>
public sealed record PlanDocumentResponse(
    string SchemaVersion,
    PlanStatementResponse Summary,
    IReadOnlyList<PlanStatementResponse> Steps,
    IReadOnlyList<PlanFileResponse> FilesToChange,
    IReadOnlyList<PlanStatementResponse> Risks,
    IReadOnlyList<PlanStatementResponse> Verification,
    IReadOnlyList<PlanStatementResponse> Unknowns,
    IReadOnlyList<PlanEvidenceResponse> Evidence);

/// <summary>What validation removed from the model's proposal, as counts. These parts are not in the plan.</summary>
/// <param name="Citations">Different cited locations that were discarded.</param>
/// <param name="SummaryReplaced">The model's summary was not kept; a fixed sentence stands in its place.</param>
public sealed record PlanTrimmedResponse(
    int Citations,
    int Steps,
    int Files,
    int Risks,
    int Verification,
    int Unknowns,
    bool SummaryReplaced);

/// <summary>Present only when the plan was read with <c>?checkIssue=true</c>.</summary>
/// <param name="Status">
/// <c>unchanged</c>: the issue's title, body and first comments are what the plan was made from.
/// <c>changed</c>: they are not. <c>unavailable</c>: the issue could not be compared.
/// </param>
/// <param name="Code">Why the status is <c>unavailable</c>: a fixed name such as <c>issue_not_found</c> or <c>github_unavailable</c>.</param>
public sealed record PlanIssueCheckResponse(string Status, string? Code);

/// <param name="State">
/// <c>queued</c> or <c>running</c> (unfinished: poll), <c>planned</c>, <c>insufficient_evidence</c>,
/// <c>failed</c> or <c>cancelled</c>.
/// </param>
/// <param name="JobId">The background job making the plan; use it to cancel.</param>
/// <param name="ErrorCode">Present only when <paramref name="State"/> is <c>failed</c>: a fixed name such as <c>issue_not_found</c>.</param>
/// <param name="Plan">Present when <paramref name="State"/> is <c>planned</c> or <c>insufficient_evidence</c>.</param>
/// <param name="Trimmed">Present with <paramref name="Plan"/>: what validation removed. It is not part of the plan document.</param>
/// <param name="Limitations">Fixed sentences written by the server saying why the outcome is what it is.</param>
/// <param name="ReviewedAt">When a person first acknowledged having read the plan. Not an approval; nothing outside TRACE changed.</param>
public sealed record PlanResponse(
    Guid PlanId,
    Guid InvestigationId,
    Guid? JobId,
    Guid RepositoryId,
    Guid SnapshotId,
    string CommitSha,
    PlanIssueResponse Issue,
    string State,
    string? ErrorCode,
    string? ModelId,
    string? PromptVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    UsageResponse Usage,
    PlanDocumentResponse? Plan,
    PlanTrimmedResponse? Trimmed,
    IReadOnlyList<string> Limitations,
    DateTimeOffset? ReviewedAt,
    PlanIssueCheckResponse? IssueCheck);
