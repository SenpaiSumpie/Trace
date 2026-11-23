using System.Globalization;
using System.Text;
using Trace.Application.Issues;

namespace Trace.Infrastructure.GitHub;

/// <summary>
/// A position in a list of issues, handed to the client as an opaque cursor: which page of the
/// underlying list, and how many of its entries have already been returned.
/// </summary>
internal readonly record struct IssueCursor(int Page, int Offset)
{
    private const int MaxPage = 100_000;

    public static IssueCursor Parse(string? cursor, int pageSize)
    {
        if (cursor is null)
        {
            return new IssueCursor(1, 0);
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - (cursor.Length % 4)) % 4);
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(padded)).Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                && page is >= 1 and <= MaxPage
                && offset < pageSize)
            {
                return new IssueCursor(page, offset);
            }
        }
        catch (FormatException)
        {
            // Falls through: not a cursor this API issued.
        }

        throw new IssueReadException(IssueReadError.InvalidCursor);
    }

    public override string ToString() =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Page}:{Offset}")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Reads the issues of one GitHub repository with a short-lived installation token that is
/// asked for read access to issues of that one repository. GET requests only. Pull requests,
/// which GitHub lists among issues, are left out.
/// </summary>
internal sealed class GitHubIssueSource(
    GitHubClient gitHub,
    long installationId,
    long gitHubRepoId,
    string owner,
    string name) : IIssueSource
{
    /// <summary>A request for one page of issues asks GitHub for at most this many of its pages.</summary>
    public const int MaxUpstreamPagesPerRequest = 3;

    public string Kind => "github";

    public async Task<RawIssuePage> ListOpenAsync(int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        var position = IssueCursor.Parse(cursor, GitHubClient.IssuePageSize);
        var token = await CreateTokenAsync(cancellationToken);
        var items = new List<RawIssue>();

        for (var fetched = 0; fetched < MaxUpstreamPagesPerRequest; fetched++)
        {
            IReadOnlyList<GitHubIssue> upstream;
            try
            {
                upstream = await gitHub.ListOpenIssuesPageAsync(token, owner, name, position.Page, cancellationToken);
            }
            catch (GitHubApiException exception)
            {
                throw await ExplainAsync(exception, token, cancellationToken)
                    ?? new IssueReadException(IssueReadError.GitHubUnavailable, exception);
            }

            var lastPage = upstream.Count < GitHubClient.IssuePageSize;
            for (var index = position.Offset; index < upstream.Count; index++)
            {
                if (upstream[index].IsPullRequest)
                {
                    continue;
                }

                items.Add(ToRaw(upstream[index]));
                if (items.Count == limit)
                {
                    var consumedAll = index + 1 == upstream.Count;
                    return new RawIssuePage(
                        items,
                        consumedAll && lastPage
                            ? null
                            : (consumedAll ? new IssueCursor(position.Page + 1, 0) : new IssueCursor(position.Page, index + 1)).ToString());
                }
            }

            if (lastPage)
            {
                return new RawIssuePage(items, null);
            }

            position = new IssueCursor(position.Page + 1, 0);
        }

        // Pull requests filled the pages that were read: hand back what was found and where to go on.
        return new RawIssuePage(items, position.ToString());
    }

    public async Task<RawIssueThread?> GetAsync(int number, int maxComments, CancellationToken cancellationToken = default)
    {
        var token = await CreateTokenAsync(cancellationToken);
        try
        {
            var issue = await gitHub.GetIssueAsync(token, owner, name, number, cancellationToken);
            if (issue.IsPullRequest || issue.Number != number)
            {
                return null;
            }

            var comments = issue.CommentCount > 0 && maxComments > 0
                ? await gitHub.ListIssueCommentsAsync(token, owner, name, number, maxComments, cancellationToken)
                : [];
            return new RawIssueThread(
                ToRaw(issue),
                [.. comments.Select(c => new RawIssueComment(c.Id, c.Author, c.Body, c.CreatedAt))]);
        }
        catch (GitHubApiException exception)
        {
            if (await ExplainAsync(exception, token, cancellationToken) is { } explained)
            {
                throw explained;
            }

            // GitHub answers 404 for a number that does not exist, 410 for a deleted issue, and
            // redirects one that was moved to another repository. None of them is an issue here.
            return exception.Status is 404 or 410 || exception.Redirected
                ? null
                : throw new IssueReadException(IssueReadError.GitHubUnavailable, exception);
        }
    }

    private async Task<string> CreateTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await gitHub.CreateInstallationTokenAsync(installationId, gitHubRepoId, cancellationToken, GitHubTokenScope.Issues);
        }
        catch (GitHubApiException exception)
        {
            // 422: the token asked for a permission this installation has not granted.
            throw new IssueReadException(
                exception.RateLimited ? IssueReadError.GitHubRateLimited
                : exception.Status == 422 ? IssueReadError.IssuesPermissionMissing
                : IssueReadError.GitHubUnavailable,
                exception);
        }
    }

    /// <summary>
    /// Turns a refusal into the specific reason, when there is one: a rate limit, a missing
    /// permission (403), or issues being switched off, which GitHub reports as 404 or 410 and
    /// which the repository's own <c>has_issues</c> setting settles.
    /// </summary>
    private async Task<IssueReadException?> ExplainAsync(GitHubApiException exception, string token, CancellationToken cancellationToken)
    {
        if (exception.RateLimited)
        {
            return new IssueReadException(IssueReadError.GitHubRateLimited, exception);
        }

        if (exception.Status == 403)
        {
            return new IssueReadException(IssueReadError.IssuesPermissionMissing, exception);
        }

        if (exception.Status is not (404 or 410))
        {
            return null;
        }

        try
        {
            return await gitHub.HasIssuesEnabledAsync(token, owner, name, cancellationToken)
                ? null
                : new IssueReadException(IssueReadError.IssuesDisabled, exception);
        }
        catch (GitHubApiException)
        {
            return new IssueReadException(IssueReadError.GitHubUnavailable, exception);
        }
    }

    private static RawIssue ToRaw(GitHubIssue issue) =>
        new(issue.Number, issue.Title, issue.State, issue.Author, issue.Labels, issue.Body, issue.CommentCount, issue.CreatedAt, issue.UpdatedAt);
}
