using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Trace.Application.Issues;
using Trace.Domain;
using Trace.Infrastructure.GitHub;
using Trace.Infrastructure.Persistence;

namespace Trace.Infrastructure.Issues;

/// <summary>
/// Issues of the demo repository, read from a directory of JSON files (one issue per file) so
/// the flow works without GitHub. The files are invented content, but they are treated exactly
/// like issues from GitHub: the same limits and cleaning apply to them afterwards.
/// </summary>
public sealed class FixtureIssueSource(string directory) : IIssueSource
{
    public const int MaxFiles = 200;
    public const int MaxFileBytes = 1024 * 1024;

    public string Kind => "fixture";

    public async Task<RawIssuePage> ListOpenAsync(int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        // The whole list is one "page" of at most MaxFiles entries; the cursor is an offset into it.
        var position = IssueCursor.Parse(cursor, MaxFiles + 1);
        if (position.Page != 1)
        {
            throw new IssueReadException(IssueReadError.InvalidCursor);
        }

        var open = (await LoadAsync(cancellationToken))
            .Where(issue => !issue.PullRequest && issue.State == "open")
            .OrderByDescending(issue => issue.CreatedAt).ThenByDescending(issue => issue.Number)
            .ToList();
        var items = open.Skip(position.Offset).Take(limit).Select(ToRaw).ToList();
        var next = position.Offset + items.Count;
        return new RawIssuePage(items, next < open.Count ? new IssueCursor(1, next).ToString() : null);
    }

    public async Task<RawIssueThread?> GetAsync(int number, int maxComments, CancellationToken cancellationToken = default)
    {
        var issue = (await LoadAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Number == number);
        return issue is null || issue.PullRequest
            ? null
            : new RawIssueThread(
                ToRaw(issue),
                [.. (issue.Comments ?? []).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(maxComments)
                    .Select(c => new RawIssueComment(c.Id, c.Author, c.Body, c.CreatedAt))]);
    }

    private async Task<List<FixtureIssue>> LoadAsync(CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists)
        {
            throw new IssueReadException(IssueReadError.IssuesUnavailable);
        }

        var issues = new List<FixtureIssue>();
        foreach (var file in root.EnumerateFiles("*.json").OrderBy(f => f.Name, StringComparer.Ordinal).Take(MaxFiles))
        {
            // Never follow a link out of the directory, and never read an unreasonably large file.
            if (file.LinkTarget is not null || file.Length > MaxFileBytes)
            {
                continue;
            }

            try
            {
                await using var stream = file.OpenRead();
                if (await JsonSerializer.DeserializeAsync<FixtureIssue>(stream, JsonSerializerOptions.Web, cancellationToken) is { Number: > 0 } issue)
                {
                    issues.Add(issue);
                }
            }
            catch (JsonException exception)
            {
                throw new IssueReadException(IssueReadError.IssuesUnavailable, exception);
            }
        }

        return issues;
    }

    private static RawIssue ToRaw(FixtureIssue issue) =>
        new(
            issue.Number,
            issue.Title,
            issue.State,
            issue.Author,
            issue.Labels ?? [],
            issue.Body,
            issue.Comments?.Count ?? 0,
            issue.CreatedAt,
            issue.UpdatedAt ?? issue.CreatedAt);

    private sealed record FixtureComment(long Id, string? Author, string? Body, DateTimeOffset CreatedAt);

    private sealed record FixtureIssue(
        int Number,
        string? Title,
        string? State,
        string? Author,
        IReadOnlyList<string?>? Labels,
        string? Body,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        IReadOnlyList<FixtureComment>? Comments,
        bool PullRequest = false);
}

/// <summary>
/// Where a repository's issues are read from: GitHub (through its active app installation) or,
/// for the demo repository, the configured directory of fixture issues. Anything else has none.
/// </summary>
internal sealed class ConfiguredIssueSourceResolver(
    TraceDbContext db,
    GitHubClient gitHub,
    IConfiguration configuration,
    IHostEnvironment environment) : IIssueSourceResolver
{
    public async Task<IIssueSource?> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken = default)
    {
        var linked = await (
            from repository in db.Repositories.AsNoTracking()
            join installation in db.GitHubInstallations.AsNoTracking() on repository.InstallationId equals installation.Id
            where repository.Id == repositoryId
            select new { repository, installation }).SingleOrDefaultAsync(cancellationToken);
        if (linked is not null)
        {
            return linked.installation.Status == InstallationStatus.Active && linked.repository.GitHubRepoId is { } gitHubRepoId
                ? new GitHubIssueSource(
                    gitHub, linked.installation.InstallationId, gitHubRepoId, linked.repository.Owner, linked.repository.Name)
                : null;
        }

        if (configuration[InfrastructureServiceCollectionExtensions.DemoIssuesPathKey] is not { Length: > 0 } path)
        {
            return null;
        }

        // Only the local demo repository: a repository that came from GitHub never falls back to files.
        var isDemo = await db.Repositories.AnyAsync(
            r => r.Id == repositoryId
                && r.InstallationId == null
                && r.GitHubRepoId == null
                && r.Owner == DemoData.RepositoryOwner
                && r.Name == DemoData.RepositoryName,
            cancellationToken);

        return isDemo ? new FixtureIssueSource(Path.GetFullPath(path, environment.ContentRootPath)) : null;
    }
}
