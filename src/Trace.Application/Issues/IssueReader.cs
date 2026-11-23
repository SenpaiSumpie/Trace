using System.Diagnostics;
using Trace.Application.Investigations;
using Trace.Application.Telemetry;

namespace Trace.Application.Issues;

/// <summary>Why issues could not be read. The names are fixed: they appear in responses and telemetry.</summary>
public enum IssueReadError
{
    /// <summary>The repository does not exist or the caller may not see it.</summary>
    RepositoryNotFound,

    /// <summary>No issue has that number; a pull request is not an issue.</summary>
    IssueNotFound,

    InvalidCursor,

    /// <summary>The GitHub App installation has not granted read access to issues.</summary>
    IssuesPermissionMissing,

    /// <summary>Issues are switched off for the repository on GitHub.</summary>
    IssuesDisabled,

    /// <summary>The repository has no place to read issues from.</summary>
    IssuesUnavailable,

    GitHubRateLimited,

    GitHubUnavailable,
}

public sealed class IssueReadException(IssueReadError error, Exception? innerException = null)
    : Exception($"Issues could not be read: {error}.", innerException)
{
    public IssueReadError Error { get; } = error;

    /// <summary>The fixed name of the error, as used in problem responses and telemetry.</summary>
    public string Code => Error switch
    {
        IssueReadError.RepositoryNotFound => "repository_not_found",
        IssueReadError.IssueNotFound => "issue_not_found",
        IssueReadError.InvalidCursor => "invalid_cursor",
        IssueReadError.IssuesPermissionMissing => "issues_permission_missing",
        IssueReadError.IssuesDisabled => "issues_disabled",
        IssueReadError.IssuesUnavailable => "issues_unavailable",
        IssueReadError.GitHubRateLimited => "github_rate_limited",
        _ => "github_unavailable",
    };
}

/// <summary>
/// Where one repository's issues are read from. Read-only by construction: there is no member
/// that creates, edits, closes or comments. A source reports a failure as an
/// <see cref="IssueReadException"/> and an unknown issue number as null.
/// </summary>
public interface IIssueSource
{
    /// <summary>A fixed name for telemetry: <c>github</c> or <c>fixture</c>.</summary>
    string Kind { get; }

    /// <summary>Open issues, newest first, never pull requests. A page may hold fewer than <paramref name="limit"/>.</summary>
    Task<RawIssuePage> ListOpenAsync(int limit, string? cursor, CancellationToken cancellationToken = default);

    /// <summary>One issue with its first <paramref name="maxComments"/> comments, or null if there is no such issue.</summary>
    Task<RawIssueThread?> GetAsync(int number, int maxComments, CancellationToken cancellationToken = default);
}

/// <summary>Finds the issue source of a repository; null when it has none. Does not check who is asking.</summary>
public interface IIssueSourceResolver
{
    Task<IIssueSource?> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lists and fetches issues of a repository the caller can see. The visibility check comes
/// first, so nothing is fetched for a repository the caller may not see. Everything returned
/// has been cleaned and cut to <see cref="IssueLimits"/>. Issue text is attacker-controlled:
/// it is returned as data only, is not stored, is not sent to any model here, and never
/// reaches a span, a metric or a log.
/// </summary>
public sealed class IssueReader(IRepositoryCatalog repositories, IIssueSourceResolver sources)
{
    public Task<IssuePage> ListOpenAsync(
        Guid repositoryId, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        ReadAsync("list", repositoryId, callerMustSee: true, async (source, activity) =>
        {
            if (limit is < 1 or > IssueLimits.MaxPageSize || cursor is { Length: > IssueLimits.MaxCursorLength })
            {
                throw new IssueReadException(IssueReadError.InvalidCursor);
            }

            var page = await source.ListOpenAsync(limit, string.IsNullOrEmpty(cursor) ? null : cursor, cancellationToken);
            var items = page.Items.Take(limit).Select(Summarize).ToList();
            activity?.SetTag("trace.issues.count", items.Count);
            activity?.SetTag("trace.issues.has_more", page.NextCursor is not null);
            return new IssuePage(items, page.NextCursor);
        });

    public Task<IssueDetail> GetAsync(Guid repositoryId, int number, CancellationToken cancellationToken = default) =>
        GetAsync(repositoryId, number, callerMustSee: true, cancellationToken);

    /// <summary>
    /// Fetches an issue for a background run. A worker has no caller: the run was authorized
    /// when it was queued (<see cref="Access.ICurrentUser"/>), so the visibility check is not
    /// repeated here. Everything else is the same: the same source, limits, cleaning and
    /// telemetry. Never call this for a request.
    /// </summary>
    public Task<IssueDetail> GetForRunAsync(Guid repositoryId, int number, CancellationToken cancellationToken = default) =>
        GetAsync(repositoryId, number, callerMustSee: false, cancellationToken);

    private Task<IssueDetail> GetAsync(Guid repositoryId, int number, bool callerMustSee, CancellationToken cancellationToken) =>
        ReadAsync("get", repositoryId, callerMustSee, async (source, activity) =>
        {
            activity?.SetTag("trace.issue.number", number);
            var thread = number < 1 ? null : await source.GetAsync(number, IssueLimits.MaxComments, cancellationToken);
            if (thread is null || thread.Issue.Number != number)
            {
                throw new IssueReadException(IssueReadError.IssueNotFound);
            }

            var detail = Bound(thread);
            activity?.SetTag("trace.issue.comments", detail.Comments.Count);
            activity?.SetTag(
                "trace.issue.truncated",
                detail.BodyTruncated || detail.CommentsTruncated || detail.Comments.Any(comment => comment.BodyTruncated));
            return detail;
        });

    /// <summary>Applies the limits to one issue and its comments.</summary>
    public static IssueDetail Bound(RawIssueThread thread)
    {
        var body = IssueText.Clean(thread.Issue.Body, IssueLimits.MaxBodyLength, out var bodyTruncated);
        var remaining = IssueLimits.MaxTotalLength - body.Length;

        var comments = new List<IssueComment>();
        foreach (var comment in thread.Comments.Take(IssueLimits.MaxComments))
        {
            if (remaining <= 0)
            {
                break;
            }

            var text = IssueText.Clean(comment.Body, Math.Min(IssueLimits.MaxCommentLength, remaining), out var truncated);
            remaining -= text.Length;
            comments.Add(new IssueComment(
                comment.Id, Author(comment.Author), text, truncated, comment.CreatedAt));
        }

        var summary = Summarize(thread.Issue) with
        {
            CommentCount = Math.Max(Math.Max(thread.Issue.CommentCount, thread.Comments.Count), 0),
        };
        return new IssueDetail(summary, body, bodyTruncated, comments, comments.Count < summary.CommentCount);
    }

    private static IssueSummary Summarize(RawIssue issue) =>
        new(
            issue.Number,
            IssueText.CleanLine(issue.Title, IssueLimits.MaxTitleLength),
            issue.State == "open" ? "open" : "closed",
            Author(issue.Author),
            [.. issue.Labels
                .Select(label => IssueText.CleanLine(label, IssueLimits.MaxLabelLength))
                .Where(label => label.Length > 0)
                .Take(IssueLimits.MaxLabels)],
            Math.Max(issue.CommentCount, 0),
            issue.CreatedAt,
            issue.UpdatedAt);

    private static string? Author(string? login) =>
        IssueText.CleanLine(login, IssueLimits.MaxAuthorLength) is { Length: > 0 } author ? author : null;

    private async Task<T> ReadAsync<T>(
        string operation,
        Guid repositoryId,
        bool callerMustSee,
        Func<IIssueSource, Activity?, Task<T>> read)
    {
        using var activity = TraceTelemetry.Source.StartActivity($"issues.{operation}");
        activity?.SetTag("trace.repository_id", repositoryId);
        var started = Stopwatch.GetTimestamp();
        var outcome = "ok";
        var kind = "none";
        try
        {
            if (callerMustSee && !await repositories.IsVisibleAsync(repositoryId))
            {
                throw new IssueReadException(IssueReadError.RepositoryNotFound);
            }

            var source = await sources.ResolveAsync(repositoryId)
                ?? throw new IssueReadException(IssueReadError.IssuesUnavailable);
            kind = source.Kind;
            return await read(source, activity);
        }
        catch (IssueReadException exception)
        {
            outcome = exception.Code;
            throw;
        }
        catch (Exception)
        {
            outcome = "error";
            throw;
        }
        finally
        {
            // Fixed names and numbers only: never a title, a body or a comment.
            activity?.SetTag("trace.issues.source", kind);
            activity?.SetTag("trace.issues.outcome", outcome);
            if (outcome != "ok")
            {
                TraceTelemetry.Fail(activity, outcome);
            }

            TraceTelemetry.IssueReadDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("source", kind),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}
