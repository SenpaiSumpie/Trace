using System.Text.RegularExpressions;
using Trace.Application.Issues;
using Trace.Infrastructure.Issues;

namespace Trace.UnitTests;

/// <summary>
/// Issue text is attacker-controlled: what TRACE carries of it is cleaned and bounded, with
/// every cut flagged. Also the rule that nothing can write to GitHub, checked on the source.
/// </summary>
public partial class IssueRuleTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nul_characters_are_removed_and_lone_surrogates_replaced()
    {
        var cleaned = IssueText.Clean("a\0b\uD83Dc\uDE00d", 100, out var truncated);

        Assert.Equal("ab�c�d", cleaned);
        Assert.False(truncated);
    }

    [Fact]
    public void Text_at_the_limit_is_kept_whole_and_longer_text_is_cut_and_flagged()
    {
        var exact = IssueText.Clean(new string('x', 10), 10, out var exactTruncated);
        var longer = IssueText.Clean(new string('x', 11), 10, out var longerTruncated);

        Assert.Equal((10, false), (exact.Length, exactTruncated));
        Assert.Equal((10, true), (longer.Length, longerTruncated));
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // Four characters, then an emoji that would straddle the limit of five.
        var cleaned = IssueText.Clean("abcd\U0001F600efg", 5, out var truncated);

        Assert.Equal("abcd", cleaned);
        Assert.True(truncated);
        Assert.Equal("abcd\U0001F600", IssueText.Clean("abcd\U0001F600efg", 6, out _));
    }

    [Fact]
    public void Removed_characters_do_not_count_towards_the_limit()
    {
        var cleaned = IssueText.Clean("\0\0\0abc", 3, out var truncated);

        Assert.Equal(("abc", false), (cleaned, truncated));
    }

    [Fact]
    public void One_line_fields_lose_control_characters_and_are_bounded()
    {
        Assert.Equal("fix the bug  now", IssueText.CleanLine("  fix\tthe bug\r\nnow\0 ", 100));
        Assert.Equal(IssueLimits.MaxTitleLength, IssueText.CleanLine(new string('t', 5_000), IssueLimits.MaxTitleLength).Length);
        Assert.Equal(string.Empty, IssueText.CleanLine(null, 10));
    }

    [Fact]
    public void A_body_over_the_limit_is_cut_and_flagged()
    {
        var detail = IssueReader.Bound(Thread(body: new string('b', IssueLimits.MaxBodyLength + 1)));

        Assert.Equal(IssueLimits.MaxBodyLength, detail.Body.Length);
        Assert.True(detail.BodyTruncated);
        Assert.False(detail.CommentsTruncated);
    }

    [Fact]
    public void A_body_within_the_limit_is_not_flagged()
    {
        var detail = IssueReader.Bound(Thread(body: new string('b', IssueLimits.MaxBodyLength), comments: ["first", "second"]));

        Assert.False(detail.BodyTruncated);
        Assert.Equal(["first", "second"], detail.Comments.Select(c => c.Body));
        Assert.All(detail.Comments, comment => Assert.False(comment.BodyTruncated));
        Assert.False(detail.CommentsTruncated);
        Assert.Equal(2, detail.Summary.CommentCount);
    }

    [Fact]
    public void Each_comment_is_cut_to_its_own_limit_and_flagged()
    {
        var detail = IssueReader.Bound(Thread(comments: [new string('c', IssueLimits.MaxCommentLength + 500), "short"]));

        Assert.Equal([IssueLimits.MaxCommentLength, 5], detail.Comments.Select(c => c.Body.Length));
        Assert.Equal([true, false], detail.Comments.Select(c => c.BodyTruncated));
        Assert.False(detail.CommentsTruncated);
    }

    [Fact]
    public void Comments_beyond_the_count_limit_are_left_out_and_flagged()
    {
        var detail = IssueReader.Bound(Thread(comments: [.. Enumerable.Range(1, IssueLimits.MaxComments + 5).Select(i => $"comment {i}")]));

        Assert.Equal(IssueLimits.MaxComments, detail.Comments.Count);
        Assert.Equal("comment 1", detail.Comments[0].Body);
        Assert.True(detail.CommentsTruncated);
        Assert.Equal(IssueLimits.MaxComments + 5, detail.Summary.CommentCount);
    }

    [Fact]
    public void Comments_the_source_did_not_return_are_flagged_from_the_reported_count()
    {
        var detail = IssueReader.Bound(Thread(comments: ["only one"], reportedComments: 75));

        Assert.Single(detail.Comments);
        Assert.True(detail.CommentsTruncated);
        Assert.Equal(75, detail.Summary.CommentCount);
    }

    [Fact]
    public void Body_and_comments_together_stay_within_the_total_limit()
    {
        // 19,000 of body leaves 41,000: ten whole comments, an eleventh cut to 1,000, the rest dropped.
        var detail = IssueReader.Bound(Thread(
            body: new string('b', 19_000),
            comments: [.. Enumerable.Repeat(new string('c', IssueLimits.MaxCommentLength), 14)]));

        Assert.Equal(11, detail.Comments.Count);
        Assert.Equal(1_000, detail.Comments[^1].Body.Length);
        Assert.True(detail.Comments[^1].BodyTruncated);
        Assert.All(detail.Comments.Take(10), comment => Assert.False(comment.BodyTruncated));
        Assert.True(detail.CommentsTruncated);
        Assert.Equal(IssueLimits.MaxTotalLength, detail.Body.Length + detail.Comments.Sum(c => c.Body.Length));
    }

    [Fact]
    public void Titles_authors_labels_and_state_are_bounded_too()
    {
        var raw = new RawIssue(
            7,
            new string('t', 1_000),
            "weird-state",
            new string('a', 1_000),
            [.. Enumerable.Range(0, 50).Select(i => (string?)$"label-{i}-{new string('l', 500)}"), null, " "],
            "body",
            -3,
            Day,
            Day);

        var summary = IssueReader.Bound(new RawIssueThread(raw, [])).Summary;

        Assert.Equal(IssueLimits.MaxTitleLength, summary.Title.Length);
        Assert.Equal("closed", summary.State);
        Assert.Equal(IssueLimits.MaxAuthorLength, summary.Author!.Length);
        Assert.Equal(IssueLimits.MaxLabels, summary.Labels.Count);
        Assert.All(summary.Labels, label => Assert.Equal(IssueLimits.MaxLabelLength, label.Length));
        Assert.Equal(0, summary.CommentCount);
    }

    [Fact]
    public async Task Fixture_issues_list_open_issues_newest_first_without_pull_requests_or_closed_ones()
    {
        var source = new FixtureIssueSource(FixtureIssues());

        var page = await source.ListOpenAsync(100, cursor: null);

        Assert.Equal([16, 14, 11, 8, 6, 5, 3, 2], page.Items.Select(issue => issue.Number));
        Assert.Null(page.NextCursor);
        Assert.Equal("fixture", source.Kind);
    }

    [Fact]
    public async Task Fixture_issues_are_paged_by_cursor_without_gaps_or_repeats()
    {
        var source = new FixtureIssueSource(FixtureIssues());
        var numbers = new List<int>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await source.ListOpenAsync(4, cursor);
            numbers.AddRange(page.Items.Select(issue => issue.Number));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(2, pages);
        Assert.Equal([16, 14, 11, 8, 6, 5, 3, 2], numbers);
    }

    [Fact]
    public async Task Fixture_issue_is_fetched_with_its_comments_and_a_pull_request_or_unknown_number_is_not_an_issue()
    {
        var source = new FixtureIssueSource(FixtureIssues());

        var issue = await source.GetAsync(3, 30);
        var closed = await source.GetAsync(12, 30);

        Assert.Equal(2, issue!.Comments.Count);
        Assert.Single((await source.GetAsync(3, 1))!.Comments);
        Assert.Equal("closed", closed!.Issue.State);
        Assert.Null(await source.GetAsync(15, 30));
        Assert.Null(await source.GetAsync(999, 30));
    }

    [Fact]
    public async Task The_fixture_has_an_obviously_synthetic_prompt_injection_issue()
    {
        var injection = await new FixtureIssueSource(FixtureIssues()).GetAsync(14, 30);

        Assert.StartsWith("SYNTHETIC TEST ISSUE", injection!.Issue.Title, StringComparison.Ordinal);
        Assert.Contains("Ignore all previous instructions", injection.Issue.Body, StringComparison.Ordinal);
        Assert.Contains("TRACE-INJECTION-MARKER-7F3A", injection.Comments.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_issue_files_are_next_to_the_indexed_fixture_not_inside_it()
    {
        var fixture = Path.Combine(RepositoryRoot(), "tests", "fixtures", "sample-repo");

        Assert.NotEmpty(Directory.EnumerateFiles(FixtureIssues(), "*.json"));
        Assert.False(Path.GetFullPath(FixtureIssues()).StartsWith(Path.GetFullPath(fixture) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories),
            file => Path.GetRelativePath(fixture, file).Contains("issue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_missing_directory_or_a_broken_file_is_reported_and_oversized_files_are_skipped()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"trace-issues-{Guid.NewGuid():N}");
        var missing = await Assert.ThrowsAsync<IssueReadException>(() => new FixtureIssueSource(directory).ListOpenAsync(10, null));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "1.json"),
                """{ "number": 1, "title": "kept", "state": "open", "createdAt": "2026-01-01T00:00:00Z" }""");
            await File.WriteAllTextAsync(
                Path.Combine(directory, "2.json"),
                $$"""{ "number": 2, "title": "too large", "state": "open", "createdAt": "2026-01-02T00:00:00Z", "body": "{{new string('x', FixtureIssueSource.MaxFileBytes)}}" }""");
            await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "not an issue");

            var page = await new FixtureIssueSource(directory).ListOpenAsync(10, null);
            Assert.Equal([1], page.Items.Select(issue => issue.Number));

            await File.WriteAllTextAsync(Path.Combine(directory, "3.json"), "{ not json");
            var broken = await Assert.ThrowsAsync<IssueReadException>(() => new FixtureIssueSource(directory).ListOpenAsync(10, null));

            Assert.Equal(IssueReadError.IssuesUnavailable, missing.Error);
            Assert.Equal(IssueReadError.IssuesUnavailable, broken.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("!!!!")]
    [InlineData("MTo5OTk5")] // "1:9999": an offset no page has
    [InlineData("Mjow")] // "2:0": the fixture has one page
    public async Task A_cursor_this_api_did_not_issue_is_refused(string cursor)
    {
        var exception = await Assert.ThrowsAsync<IssueReadException>(
            () => new FixtureIssueSource(FixtureIssues()).ListOpenAsync(10, cursor));

        Assert.Equal(IssueReadError.InvalidCursor, exception.Error);
    }

    /// <summary>
    /// Nothing TRACE does may write to GitHub. The GitHub client is the only code that talks to
    /// GitHub, so its source is read: the only requests that are not GET are the two POSTs
    /// GitHub's authentication requires, and no other HTTP method appears at all.
    /// </summary>
    [Fact]
    public void The_github_client_can_only_get_apart_from_the_two_authentication_posts()
    {
        var gitHubDirectory = Path.Combine(RepositoryRoot(), "src", "Trace.Infrastructure", "GitHub");
        var client = File.ReadAllText(Path.Combine(gitHubDirectory, "GitHubClient.cs"));

        var posts = PostRequest().Matches(client).Select(match => match.Groups["path"].Value).ToList();
        Assert.Equal(["login/oauth/access_token", "app/installations/{installationId}/access_tokens"], posts);
        Assert.Equal(2, HttpMethodUse().Matches(client).Count(match => match.Groups["method"].Value == "Post"));
        Assert.All(posts, path => Assert.DoesNotContain("issues", path, StringComparison.OrdinalIgnoreCase));

        // Everywhere TRACE could reach GitHub or issues from: no other method, by any spelling.
        var sources = new[] { "src", "tools" }
            .SelectMany(top => Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), top), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            var readOnlyCode = name != "GitHubClient.cs"
                && (file.StartsWith(gitHubDirectory, StringComparison.Ordinal)
                    || name.Contains("Issue", StringComparison.Ordinal)
                    || name.Contains("Plan", StringComparison.Ordinal)
                    || name.Contains("Markdown", StringComparison.Ordinal));
            string[] allowed = readOnlyCode ? ["Get"] : ["Get", "Post"];

            Assert.All(HttpMethodUse().Matches(text), match => Assert.Contains(match.Groups["method"].Value, allowed));
            Assert.DoesNotMatch(WriteCall(), text);
        }
    }

    /// <summary>
    /// Creating, reading, reviewing and exporting a plan have no way to reach GitHub of their
    /// own. The plan code (the workflow, the service behind the endpoints, the export) and the
    /// plan endpoints never name an HTTP client or the GitHub client: the one thing they read
    /// from outside TRACE is an issue, and only through <see cref="IssueReader"/>, whose
    /// sources can only GET (the test above).
    /// </summary>
    [Fact]
    public void Plan_code_and_plan_endpoints_reach_github_only_through_the_read_only_issue_reader()
    {
        var root = RepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Trace.Application", "Plans"), "*.cs")
            .Append(Path.Combine(root, "src", "Trace.Api", "V1Endpoints.Plans.cs"))
            .Append(Path.Combine(root, "src", "Trace.Infrastructure", "Persistence", "EfPlanStore.cs"))
            .ToList();

        Assert.Contains(files, file => Path.GetFileName(file) == "PlanService.cs");
        Assert.Contains(files, file => Path.GetFileName(file) == "PlanMarkdown.cs");
        Assert.All(files, file =>
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(OutboundHttp(), text);
            Assert.Empty(HttpMethodUse().Matches(text));
        });

        // Reviewing and exporting do not even read an issue: the review and the export are
        // written without the issue reader, which only the optional issue check uses.
        var service = File.ReadAllText(Path.Combine(root, "src", "Trace.Application", "Plans", "PlanService.cs"));
        Assert.Single(IssueReaderCall().Matches(service));
        Assert.DoesNotMatch(OutboundHttp(), File.ReadAllText(Path.Combine(root, "src", "Trace.Application", "Plans", "PlanMarkdown.cs")));
        Assert.DoesNotContain("issues.", File.ReadAllText(Path.Combine(root, "src", "Trace.Api", "V1Endpoints.Plans.cs")), StringComparison.Ordinal);
    }

    private static RawIssueThread Thread(string body = "body", string[]? comments = null, int? reportedComments = null) =>
        new(
            new RawIssue(7, "title", "open", "author", ["bug"], body, reportedComments ?? comments?.Length ?? 0, Day, Day),
            [.. (comments ?? []).Select((text, index) => new RawIssueComment(index + 1, "commenter", text, Day.AddMinutes(index)))]);

    private static string FixtureIssues() => Path.Combine(RepositoryRoot(), "tests", "fixtures", "sample-repo.issues");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trace.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Trace.slnx.");
    }

    [GeneratedRegex(@"\bHttpClient\b|\bIHttpClientFactory\b|\bHttpRequestMessage\b|\bGitHubClient\b|\bGitHubIssueSource\b|\bWebRequest\b|\bSocket\b")]
    private static partial Regex OutboundHttp();

    [GeneratedRegex(@"\bissues\.\w+Async\(")]
    private static partial Regex IssueReaderCall();

    [GeneratedRegex(@"HttpMethod\.(?<method>[A-Z]\w*)")]
    private static partial Regex HttpMethodUse();

    [GeneratedRegex(@"HttpMethod\.Post,\s*new Uri\(options\.\w+, \$?""(?<path>[^""]+)""\)")]
    private static partial Regex PostRequest();

    [GeneratedRegex(@"\b(Post|Put|Patch|Delete)(AsJson)?Async\s*\(|new HttpMethod\(|HttpMethod\.Parse\(")]
    private static partial Regex WriteCall();
}
