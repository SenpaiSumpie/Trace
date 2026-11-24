using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trace.Application.Issues;
using Trace.Application.Telemetry;
using Trace.Contracts.V1;
using Trace.Domain;
using Trace.Infrastructure.GitHub;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>
/// Listing and fetching issues. GitHub's HTTP API is replaced by a stub that answers in
/// GitHub's documented formats and records every request; the demo repository is served from
/// the fixture files. Never run against real GitHub issues.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IssueApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const long InstallationId = 101;
    private const long PrivateRepoId = 5001;
    private const string TitleMarker = "zztitle-marker";
    private const string BodyMarker = "zzbody-marker";
    private const string CommentMarker = "zzcomment-marker";
    private const string LabelMarker = "zzlabel-marker";
    private const string AuthorMarker = "zzauthor-marker";
    private const string InjectionMarker = "TRACE-INJECTION-MARKER-7F3A";

    private readonly WebApplicationFactory<Program> baseFactory = new();
    private readonly FakeGitHub gitHub = new();
    private readonly CapturedLogs logs = new();
    private TestDatabase database = null!;
    private WebApplicationFactory<Program> factory = null!;

    public async Task InitializeAsync()
    {
        database = await postgres.CreateDatabaseAsync(migrate: false);
        factory = baseFactory.WithDatabase(database.ConnectionString, initializeOnStartup: true).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:ClientId"] = "Iv1.testclient",
                ["GitHub:ClientSecret"] = "client-secret",
                ["GitHub:PrivateKey"] = RSA.Create(2048).ExportRSAPrivateKeyPem(),
                ["Auth:AllowAnonymousWrites"] = "false",

                // Everything the application would ever log, so the telemetry test sees all of it.
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:System.Net.Http"] = "Trace",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(GitHubClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gitHub);
                services.AddSingleton<ILoggerProvider>(logs);
            });
        });
    }

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await baseFactory.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task Demo_repository_serves_its_fixture_issues_to_anyone_without_any_github_call()
    {
        using var anonymous = NewClient();
        var demo = Assert.Single((await anonymous.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);

        using var listResponse = await anonymous.GetAsync($"/api/v1/repositories/{demo.Id}/issues");
        var listJson = await listResponse.Content.ReadAsStringAsync();
        var list = JsonSerializer.Deserialize<IssueListResponse>(listJson, JsonSerializerOptions.Web)!;
        var issue = await anonymous.GetFromJsonAsync<IssueResponse>($"/api/v1/repositories/{demo.Id}/issues/3");
        var closed = await anonymous.GetFromJsonAsync<IssueResponse>($"/api/v1/repositories/{demo.Id}/issues/12");
        using var pullRequest = await anonymous.GetAsync($"/api/v1/repositories/{demo.Id}/issues/15");
        using var missing = await anonymous.GetAsync($"/api/v1/repositories/{demo.Id}/issues/999");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal([16, 14, 11, 8, 6, 5, 3, 2], list.Items.Select(i => i.Number));
        Assert.Null(list.NextCursor);
        Assert.All(list.Items, item => Assert.Equal("open", item.State));
        Assert.DoesNotContain("\"body\"", listJson, StringComparison.Ordinal);

        Assert.Equal(("First page of the task list skips the oldest tasks", "open", "dana-fixture", 2), (issue!.Title, issue.State, issue.Author, issue.CommentCount));
        Assert.Equal(["bug"], issue.Labels);
        Assert.Contains("page=1&pageSize=20", issue.Body, StringComparison.Ordinal);
        Assert.Equal([3001L, 3002L], issue.Comments.Select(c => c.Id));
        Assert.Equal((false, false), (issue.BodyTruncated, issue.CommentsTruncated));
        Assert.Equal("closed", closed!.State);
        await AssertProblemAsync(pullRequest, HttpStatusCode.NotFound, "issue_not_found");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "issue_not_found");

        Assert.Empty(gitHub.Requests);
    }

    [Fact]
    public async Task Demo_issue_list_is_paged_by_cursor_and_rejects_a_bad_limit_or_cursor()
    {
        using var client = NewClient();
        var demo = Assert.Single((await client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);
        var url = $"/api/v1/repositories/{demo.Id}/issues";

        var pages = new List<IssueListResponse>();
        string? cursor = null;
        do
        {
            var page = await client.GetFromJsonAsync<IssueListResponse>($"{url}?limit=4{(cursor is null ? string.Empty : $"&cursor={cursor}")}");
            pages.Add(page!);
            cursor = page!.NextCursor;
        }
        while (cursor is not null && pages.Count < 10);

        using var zero = await client.GetAsync($"{url}?limit=0");
        using var tooMany = await client.GetAsync($"{url}?limit=101");
        using var notANumber = await client.GetAsync($"{url}?limit=many");
        using var badCursor = await client.GetAsync($"{url}?cursor=not-a-cursor");
        using var longCursor = await client.GetAsync($"{url}?cursor={new string('A', 400)}");
        using var numberZero = await client.GetAsync($"{url}/0");
        using var numberText = await client.GetAsync($"{url}/abc");
        using var unknownRepository = await client.GetAsync($"/api/v1/repositories/{Guid.NewGuid()}/issues");
        using var unknownRepositoryIssue = await client.GetAsync($"/api/v1/repositories/{Guid.NewGuid()}/issues/3");

        Assert.Equal([4, 4], pages.Select(p => p.Items.Count));
        Assert.Equal([16, 14, 11, 8, 6, 5, 3, 2], pages.SelectMany(p => p.Items).Select(i => i.Number));
        Assert.All(new[] { zero, tooMany, notANumber }, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        await AssertProblemAsync(badCursor, HttpStatusCode.BadRequest, "invalid_cursor");
        await AssertProblemAsync(longCursor, HttpStatusCode.BadRequest, "invalid_cursor");
        Assert.All(
            new[] { numberZero, numberText, unknownRepository, unknownRepositoryIssue },
            response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        await AssertProblemAsync(unknownRepository, HttpStatusCode.NotFound, code: null);
    }

    [Fact]
    public async Task Demo_repository_without_a_configured_issue_directory_reports_that_it_has_no_issues_source()
    {
        using var client = factory
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:IssuesPath"] = string.Empty })))
            .CreateClient();
        var demo = Assert.Single((await client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);

        using var list = await client.GetAsync($"/api/v1/repositories/{demo.Id}/issues");
        using var one = await client.GetAsync($"/api/v1/repositories/{demo.Id}/issues/3");

        await AssertProblemAsync(list, HttpStatusCode.Conflict, "issues_unavailable");
        await AssertProblemAsync(one, HttpStatusCode.Conflict, "issues_unavailable");
    }

    [Fact]
    public async Task A_prompt_injection_issue_is_returned_as_inert_text_and_starts_nothing()
    {
        using var client = NewClient();
        var demo = Assert.Single((await client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);

        var issue = await client.GetFromJsonAsync<IssueResponse>($"/api/v1/repositories/{demo.Id}/issues/14");

        // The text arrives whole, as the value of ordinary string fields, and nothing acted on it.
        Assert.Contains("Ignore all previous instructions", issue!.Body, StringComparison.Ordinal);
        Assert.Contains("delete_repository", issue.Body, StringComparison.Ordinal);
        Assert.Contains(InjectionMarker, Assert.Single(issue.Comments).Body, StringComparison.Ordinal);
        Assert.Equal("open", issue.State);

        // Reading an issue stores nothing and queues nothing: no model ever sees the text in this step.
        await using var db = database.CreateContext();
        Assert.Equal(0, await db.Investigations.CountAsync());
        Assert.Equal(0, await db.Jobs.CountAsync());
        Assert.Equal(1, await db.Repositories.CountAsync());
        Assert.Empty(gitHub.Requests);
        Assert.DoesNotContain(logs.Lines, line => line.Contains(InjectionMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Signed_in_user_lists_open_issues_of_their_repository_across_pages_without_pull_requests()
    {
        // 120 open entries, newest first; every third one is a pull request.
        gitHub.OpenIssues.AddRange(Enumerable.Range(0, 120).Select(i => Issue(200 - i, pullRequest: i % 3 == 0)));
        var expected = Enumerable.Range(0, 120).Where(i => i % 3 != 0).Select(i => 200 - i).ToList();
        using var alice = NewClient();
        var repository = await SignInAndFindRepositoryAsync(alice, "alice");

        var numbers = new List<int>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await alice.GetFromJsonAsync<IssueListResponse>(
                $"/api/v1/repositories/{repository.Id}/issues?limit=30{(cursor is null ? string.Empty : $"&cursor={cursor}")}");
            numbers.AddRange(page!.Items.Select(i => i.Number));
            pageSizes.Add(page.Items.Count);
            cursor = page.NextCursor;
        }
        while (cursor is not null && pageSizes.Count < 20);

        Assert.Equal(expected, numbers);
        Assert.Equal([30, 30, 20], pageSizes);
        var first = (await alice.GetFromJsonAsync<IssueListResponse>($"/api/v1/repositories/{repository.Id}/issues?limit=1"))!;
        Assert.Equal((199, $"Issue 199 {TitleMarker}", "open", AuthorMarker, 0), (first.Items[0].Number, first.Items[0].Title, first.Items[0].State, first.Items[0].Author, first.Items[0].CommentCount));
        Assert.Equal([LabelMarker], first.Items[0].Labels);
        Assert.NotNull(first.NextCursor);

        // Only open issues were asked for, in pages of the client's fixed size.
        var listRequests = gitHub.Requests.Where(r => r.Path == "/repos/acme/private-repo/issues").ToList();
        Assert.NotEmpty(listRequests);
        Assert.All(listRequests, r => Assert.Contains($"state=open&sort=created&direction=desc&per_page={GitHubClient.IssuePageSize}&page=", r.Query, StringComparison.Ordinal));

        // Each token was asked for this one repository and for reading issues, nothing more.
        Assert.NotEmpty(gitHub.TokenRequests);
        Assert.All(gitHub.TokenRequests, document =>
        {
            Assert.Equal([PrivateRepoId], document.RootElement.GetProperty("repository_ids").EnumerateArray().Select(id => id.GetInt64()));
            Assert.Equal(
                ["issues=read", "metadata=read"],
                document.RootElement.GetProperty("permissions").EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}").Order());
        });
    }

    [Fact]
    public async Task A_page_filled_with_pull_requests_returns_what_it_found_and_a_cursor_to_continue()
    {
        // 200 pull requests ahead of the only issue: more than one request may read.
        gitHub.OpenIssues.AddRange(Enumerable.Range(0, 200).Select(i => Issue(500 - i, pullRequest: true)));
        gitHub.OpenIssues.Add(Issue(7));
        using var alice = NewClient();
        var repository = await SignInAndFindRepositoryAsync(alice, "alice");

        var first = await alice.GetFromJsonAsync<IssueListResponse>($"/api/v1/repositories/{repository.Id}/issues");
        var second = await alice.GetFromJsonAsync<IssueListResponse>($"/api/v1/repositories/{repository.Id}/issues?cursor={first!.NextCursor}");

        Assert.Empty(first.Items);
        Assert.NotNull(first.NextCursor);
        Assert.Equal([7], second!.Items.Select(i => i.Number));
        Assert.Null(second.NextCursor);
        Assert.Equal(5, gitHub.Requests.Count(r => r.Path == "/repos/acme/private-repo/issues"));
    }

    [Fact]
    public async Task Signed_in_user_fetches_an_issue_with_its_comments_and_a_pull_request_or_moved_issue_is_not_found()
    {
        gitHub.Issues[42] = Issue(42, body: $"The body {BodyMarker}", comments: 2);
        gitHub.Comments[42] = [Comment(1, $"first {CommentMarker}"), Comment(2, "second")];
        gitHub.Issues[43] = Issue(43, pullRequest: true);
        gitHub.Issues[44] = Issue(44, state: "closed");
        gitHub.Issues[45] = Issue(46);
        using var alice = NewClient();
        var repository = await SignInAndFindRepositoryAsync(alice, "alice");
        var url = $"/api/v1/repositories/{repository.Id}/issues";

        var issue = await alice.GetFromJsonAsync<IssueResponse>($"{url}/42");
        var closed = await alice.GetFromJsonAsync<IssueResponse>($"{url}/44");
        using var pullRequest = await alice.GetAsync($"{url}/43");
        using var missing = await alice.GetAsync($"{url}/404");
        using var wrongNumber = await alice.GetAsync($"{url}/45");
        gitHub.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/issues/77", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://api.github.com/repos/other/elsewhere/issues/9") } }
            : null;
        using var moved = await alice.GetAsync($"{url}/77");
        gitHub.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/issues/78", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                // What a handler that followed the redirect would hand back: another repository's issue.
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/other/elsewhere/issues/9"),
                Content = JsonContent.Create(Issue(78, body: "from another repository")),
            }
            : null;
        using var followed = await alice.GetAsync($"{url}/78");
        gitHub.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/issues/79", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Gone)
            : null;
        using var deleted = await alice.GetAsync($"{url}/79");

        Assert.Equal((42, $"Issue 42 {TitleMarker}", $"The body {BodyMarker}", false, 2, false), (issue!.Number, issue.Title, issue.Body, issue.BodyTruncated, issue.CommentCount, issue.CommentsTruncated));
        Assert.Equal([$"first {CommentMarker}", "second"], issue.Comments.Select(c => c.Body));
        Assert.Equal("commenter", issue.Comments[0].Author);
        Assert.Equal("closed", closed!.State);
        foreach (var response in new[] { pullRequest, missing, wrongNumber, moved, followed, deleted })
        {
            await AssertProblemAsync(response, HttpStatusCode.NotFound, "issue_not_found");
        }

        Assert.Contains(gitHub.Requests, r => r.Path == "/repos/acme/private-repo/issues/42/comments" && r.Query == $"?per_page={IssueLimits.MaxComments}&page=1");
        Assert.DoesNotContain(gitHub.Requests, r => r.Path.Contains("/other/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Size_limits_are_enforced_on_what_github_returns_and_every_cut_is_flagged()
    {
        gitHub.Issues[50] = Issue(50, body: "start\0" + new string('b', 70_000), comments: 40);
        gitHub.Comments[50] = [.. Enumerable.Range(1, 40).Select(i => Comment(i, i == 1 ? "small\0 one" : new string('c', 5_000)))];
        using var alice = NewClient();
        var repository = await SignInAndFindRepositoryAsync(alice, "alice");

        using var response = await alice.GetAsync($"/api/v1/repositories/{repository.Id}/issues/50");
        var json = await response.Content.ReadAsStringAsync();
        var issue = JsonSerializer.Deserialize<IssueResponse>(json, JsonSerializerOptions.Web)!;

        Assert.Equal((IssueLimits.MaxBodyLength, true), (issue.Body.Length, issue.BodyTruncated));
        Assert.StartsWith("startbbb", issue.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0000", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(("small one", false), (issue.Comments[0].Body, issue.Comments[0].BodyTruncated));
        Assert.All(issue.Comments, comment => Assert.True(comment.Body.Length <= IssueLimits.MaxCommentLength));
        Assert.All(issue.Comments.Skip(1), comment => Assert.True(comment.BodyTruncated));
        Assert.True(issue.Comments.Count <= IssueLimits.MaxComments);
        Assert.True(issue.Body.Length + issue.Comments.Sum(c => c.Body.Length) <= IssueLimits.MaxTotalLength);

        // 20,000 of body leaves 40,000: the nine-character comment, nine cut to 4,000, one cut to what is left.
        Assert.Equal(11, issue.Comments.Count);
        Assert.Equal(IssueLimits.MaxTotalLength, issue.Body.Length + issue.Comments.Sum(c => c.Body.Length));
        Assert.Equal((40, true), (issue.CommentCount, issue.CommentsTruncated));

        // GitHub was asked for no more comments than the limit, however many the issue has.
        Assert.Contains(gitHub.Requests, r => r.Path.EndsWith("/issues/50/comments", StringComparison.Ordinal) && r.Query.Contains($"per_page={IssueLimits.MaxComments}&", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_response_larger_than_the_byte_limit_is_refused_rather_than_read()
    {
        gitHub.Issues[60] = Issue(60, body: new string('x', GitHubClient.MaxIssueResponseBytes + 1));
        using var alice = NewClient();
        var repository = await SignInAndFindRepositoryAsync(alice, "alice");

        using var response = await alice.GetAsync($"/api/v1/repositories/{repository.Id}/issues/60");

        await AssertProblemAsync(response, HttpStatusCode.BadGateway, "github_unavailable");
    }

    [Fact]
    public async Task Another_user_or_an_anonymous_caller_gets_404_and_github_is_never_asked()
    {
        gitHub.OpenIssues.Add(Issue(1));
        gitHub.Issues[1] = Issue(1);
        using var alice = NewClient();
        using var bob = NewClient();
        using var anonymous = NewClient();
        var repository = await SignInAndFindRepositoryAsync(alice, "alice");
        await SignInAsync(bob, "bob");
        var before = gitHub.Requests.Count;

        using var bobList = await bob.GetAsync($"/api/v1/repositories/{repository.Id}/issues");
        using var bobIssue = await bob.GetAsync($"/api/v1/repositories/{repository.Id}/issues/1");
        using var anonymousList = await anonymous.GetAsync($"/api/v1/repositories/{repository.Id}/issues");
        using var anonymousIssue = await anonymous.GetAsync($"/api/v1/repositories/{repository.Id}/issues/1");

        foreach (var response in new[] { bobList, bobIssue, anonymousList, anonymousIssue })
        {
            // The same plain 404 as a repository that does not exist: no code, no detail.
            await AssertProblemAsync(response, HttpStatusCode.NotFound, code: null);
        }

        Assert.Equal(before, gitHub.Requests.Count);

        // Alice can; and once her installation is suspended, she cannot either.
        using var aliceList = await alice.GetAsync($"/api/v1/repositories/{repository.Id}/issues");
        Assert.Equal(HttpStatusCode.OK, aliceList.StatusCode);
        await using var db = database.CreateContext();
        (await db.GitHubInstallations.SingleAsync()).SetStatus(InstallationStatus.Suspended);
        await db.SaveChangesAsync();
        using var suspended = await alice.GetAsync($"/api/v1/repositories/{repository.Id}/issues");
        Assert.Equal(HttpStatusCode.NotFound, suspended.StatusCode);
    }

    [Fact]
    public async Task A_missing_issues_permission_is_a_specific_409_not_a_500()
    {
        gitHub.OpenIssues.Add(Issue(1));
        gitHub.Issues[1] = Issue(1);
        using var alice = NewClient();
        var repository = await SignInAndFindRepositoryAsync(alice, "alice");
        var url = $"/api/v1/repositories/{repository.Id}/issues";

        // The installation has not accepted the permission: GitHub refuses the token (422).
        gitHub.GrantsIssuesPermission = false;
        using var listRefused = await alice.GetAsync(url);
        using var issueRefused = await alice.GetAsync($"{url}/1");

        // The token is issued but the call is refused (403 "Resource not accessible by integration").
        gitHub.GrantsIssuesPermission = true;
        gitHub.Override = request => request.RequestUri!.AbsolutePath.Contains("/issues", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"Resource not accessible by integration"}""") }
            : null;
        using var listForbidden = await alice.GetAsync(url);
        using var issueForbidden = await alice.GetAsync($"{url}/1");

        foreach (var response in new[] { listRefused, issueRefused, listForbidden, issueForbidden })
        {
            var detail = await AssertProblemAsync(response, HttpStatusCode.Conflict, "issues_permission_missing");
            Assert.Contains("has not granted read access to issues", detail, StringComparison.Ordinal);
        }

        Assert.Contains(logs.Lines, line => line.Contains($"Issues of repository {repository.Id} could not be read: issues_permission_missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Issues_switched_off_rate_limits_and_github_failures_each_have_their_own_answer()
    {
        gitHub.Issues[1] = Issue(1);
        using var alice = NewClient();
        var repository = await SignInAndFindRepositoryAsync(alice, "alice");
        var url = $"/api/v1/repositories/{repository.Id}/issues";

        gitHub.HasIssues = false;
        gitHub.Override = IssueCalls(() => new HttpResponseMessage(HttpStatusCode.Gone));
        using var disabledList = await alice.GetAsync(url);
        using var disabledIssue = await alice.GetAsync($"{url}/1");
        gitHub.Override = IssueCalls(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var disabledListAs404 = await alice.GetAsync(url);

        gitHub.HasIssues = true;
        using var listNotFound = await alice.GetAsync(url);

        gitHub.Override = IssueCalls(() => new HttpResponseMessage(HttpStatusCode.Forbidden) { Headers = { { "X-RateLimit-Remaining", "0" } } });
        using var primaryLimit = await alice.GetAsync(url);
        gitHub.Override = IssueCalls(() => new HttpResponseMessage(HttpStatusCode.Forbidden) { Headers = { { "Retry-After", "60" } } });
        using var secondaryLimit = await alice.GetAsync($"{url}/1");
        gitHub.Override = IssueCalls(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var tooMany = await alice.GetAsync(url);

        gitHub.Override = IssueCalls(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var serverError = await alice.GetAsync(url);
        gitHub.Override = IssueCalls(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not json</html>") });
        using var notJson = await alice.GetAsync($"{url}/1");
        gitHub.Override = IssueCalls(() => throw new HttpRequestException("connection refused"));
        using var unreachable = await alice.GetAsync(url);

        await AssertProblemAsync(disabledList, HttpStatusCode.Conflict, "issues_disabled");
        await AssertProblemAsync(disabledIssue, HttpStatusCode.Conflict, "issues_disabled");
        await AssertProblemAsync(disabledListAs404, HttpStatusCode.Conflict, "issues_disabled");
        await AssertProblemAsync(listNotFound, HttpStatusCode.BadGateway, "github_unavailable");
        await AssertProblemAsync(primaryLimit, HttpStatusCode.ServiceUnavailable, "github_rate_limited");
        await AssertProblemAsync(secondaryLimit, HttpStatusCode.ServiceUnavailable, "github_rate_limited");
        await AssertProblemAsync(tooMany, HttpStatusCode.ServiceUnavailable, "github_rate_limited");
        await AssertProblemAsync(serverError, HttpStatusCode.BadGateway, "github_unavailable");
        await AssertProblemAsync(notJson, HttpStatusCode.BadGateway, "github_unavailable");
        await AssertProblemAsync(unreachable, HttpStatusCode.BadGateway, "github_unavailable");
    }

    [Fact]
    public async Task Every_request_about_issues_is_a_get_and_nothing_but_authentication_is_ever_posted()
    {
        gitHub.OpenIssues.AddRange(Enumerable.Range(0, 60).Select(i => Issue(100 - i, pullRequest: i % 2 == 0)));
        gitHub.Issues[42] = Issue(42, comments: 1);
        gitHub.Comments[42] = [Comment(1, "a comment")];
        using var alice = NewClient();
        var repository = await SignInAndFindRepositoryAsync(alice, "alice");
        var url = $"/api/v1/repositories/{repository.Id}/issues";

        // Everything the feature does, including its failure paths.
        var page = await alice.GetFromJsonAsync<IssueListResponse>($"{url}?limit=10");
        await alice.GetFromJsonAsync<IssueListResponse>($"{url}?limit=10&cursor={page!.NextCursor}");
        await alice.GetFromJsonAsync<IssueResponse>($"{url}/42");
        using var missing = await alice.GetAsync($"{url}/999");
        gitHub.HasIssues = false;
        using var disabled = await alice.GetAsync($"{url}/998");

        // The API itself offers no way to change an issue.
        using var post = await alice.PostAsJsonAsync(url, new { title = "new" });
        using var patch = await alice.PatchAsJsonAsync($"{url}/42", new { state = "closed" });
        using var put = await alice.PutAsJsonAsync($"{url}/42", new { title = "edited" });
        using var delete = await alice.DeleteAsync($"{url}/42");
        using var comment = await alice.PostAsJsonAsync($"{url}/42/comments", new { body = "hello" });

        Assert.All(new[] { post, patch, put, delete }, response => Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode));
        Assert.Equal(HttpStatusCode.NotFound, comment.StatusCode);

        var requests = gitHub.Requests.ToList();
        var aboutIssues = requests.Where(r => r.Path.StartsWith("/repos/", StringComparison.Ordinal)).ToList();
        Assert.True(aboutIssues.Count >= 6);
        Assert.Contains(aboutIssues, r => r.Path.EndsWith("/issues/42/comments", StringComparison.Ordinal));
        Assert.All(aboutIssues, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(
            ["/app/installations/101/access_tokens", "/login/oauth/access_token"],
            requests.Where(r => r.Method != HttpMethod.Get).Select(r => r.Path).Distinct().Order());
        Assert.DoesNotContain(requests, r => r.Method == HttpMethod.Patch || r.Method == HttpMethod.Put || r.Method == HttpMethod.Delete);
        Assert.All(requests.Where(r => r.Method == HttpMethod.Post), r => Assert.DoesNotContain("issues", r.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Spans_and_logs_carry_identifiers_counts_and_outcome_names_but_never_issue_text()
    {
        gitHub.OpenIssues.Add(Issue(42, body: $"The body {BodyMarker}", comments: 1));
        gitHub.Issues[42] = Issue(42, body: $"The body {BodyMarker}", comments: 1);
        gitHub.Comments[42] = [Comment(1, $"a comment {CommentMarker}")];
        using var alice = NewClient();
        var repository = await SignInAndFindRepositoryAsync(alice, "alice");
        using var anonymous = NewClient();
        var demo = (await anonymous.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!.Single();
        var url = $"/api/v1/repositories/{repository.Id}/issues";

        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            // Every source in the process: TRACE's own spans and those of ASP.NET Core and HttpClient.
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var list = await alice.GetFromJsonAsync<IssueListResponse>(url);
        var issue = await alice.GetFromJsonAsync<IssueResponse>($"{url}/42");
        var injection = await anonymous.GetFromJsonAsync<IssueResponse>($"/api/v1/repositories/{demo.Id}/issues/14");
        using var missing = await alice.GetAsync($"{url}/999");
        gitHub.GrantsIssuesPermission = false;
        using var refused = await alice.GetAsync(url);

        // The responses did contain the text, so its absence below means something.
        Assert.Contains(TitleMarker, list!.Items[0].Title, StringComparison.Ordinal);
        Assert.Contains(BodyMarker, issue!.Body, StringComparison.Ordinal);
        Assert.Contains(CommentMarker, issue.Comments[0].Body, StringComparison.Ordinal);
        Assert.Contains(InjectionMarker, injection!.Comments[0].Body, StringComparison.Ordinal);

        var ours = spans.Where(span => span.Source.Name == TraceTelemetry.SourceName && span.OperationName.StartsWith("issues.", StringComparison.Ordinal)).ToList();
        var listSpan = ours.First(span => span.OperationName == "issues.list" && (string?)span.GetTagItem("trace.issues.outcome") == "ok");
        Assert.Equal("github", listSpan.GetTagItem("trace.issues.source"));
        Assert.Equal(1, listSpan.GetTagItem("trace.issues.count"));
        Assert.Equal(repository.Id, listSpan.GetTagItem("trace.repository_id"));
        var getSpan = ours.First(span => span.OperationName == "issues.get" && (string?)span.GetTagItem("trace.issues.source") == "github" && (string?)span.GetTagItem("trace.issues.outcome") == "ok");
        Assert.Equal(42, getSpan.GetTagItem("trace.issue.number"));
        Assert.Equal(1, getSpan.GetTagItem("trace.issue.comments"));
        Assert.Equal(false, getSpan.GetTagItem("trace.issue.truncated"));
        Assert.Contains(ours, span => (string?)span.GetTagItem("trace.issues.source") == "fixture" && (string?)span.GetTagItem("trace.issues.outcome") == "ok");
        Assert.Contains(ours, span => (string?)span.GetTagItem("trace.issues.outcome") == "issue_not_found" && span.Status == ActivityStatusCode.Error);
        Assert.Contains(ours, span => (string?)span.GetTagItem("trace.issues.outcome") == "issues_permission_missing");

        var recorded = spans
            .SelectMany(span => span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
                .Concat(span.Events.SelectMany(e => e.Tags.Select(tag => $"{tag.Key}={tag.Value}").Append(e.Name)))
                .Concat(span.Baggage.Select(item => $"{item.Key}={item.Value}"))
                .Append(span.DisplayName)
                .Append(span.StatusDescription ?? string.Empty))
            .Concat(logs.Lines)
            .ToList();
        Assert.Contains(logs.Lines, line => line.Contains("could not be read: issues_permission_missing", StringComparison.Ordinal));
        Assert.Contains(logs.Lines, line => line.Contains("/repos/acme/private-repo/issues/42", StringComparison.Ordinal));
        foreach (var forbidden in new[] { TitleMarker, BodyMarker, CommentMarker, LabelMarker, AuthorMarker, InjectionMarker, "Ignore all previous instructions", "SYNTHETIC TEST ISSUE" })
        {
            Assert.DoesNotContain(recorded, text => text.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static Func<HttpRequestMessage, HttpResponseMessage?> IssueCalls(Func<HttpResponseMessage> answer) =>
        request => request.RequestUri!.AbsolutePath.Contains("/issues", StringComparison.Ordinal) ? answer() : null;

    private static Dictionary<string, object?> Issue(int number, bool pullRequest = false, string state = "open", string? body = null, int comments = 0)
    {
        var issue = new Dictionary<string, object?>
        {
            ["id"] = 9_000_000L + number,
            ["number"] = number,
            ["title"] = $"Issue {number} {TitleMarker}",
            ["state"] = state,
            ["user"] = new { login = AuthorMarker, id = 77 },
            ["labels"] = new[] { new { id = 1, name = LabelMarker, color = "d73a4a" } },
            ["body"] = body ?? $"Body of {number} {BodyMarker}",
            ["comments"] = comments,
            ["created_at"] = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(number),
            ["updated_at"] = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero).AddMinutes(number),
            ["html_url"] = $"https://github.com/acme/private-repo/issues/{number}",
        };
        if (pullRequest)
        {
            issue["pull_request"] = new { url = $"https://api.github.com/repos/acme/private-repo/pulls/{number}" };
        }

        return issue;
    }

    private static object Comment(long id, string body) =>
        new { id, user = new { login = "commenter" }, body, created_at = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero).AddMinutes(id) };

    /// <summary>Asserts a problem response and its <c>code</c>; returns its detail.</summary>
    private static async Task<string?> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(code, problem.RootElement.TryGetProperty("code", out var actual) ? actual.GetString() : null);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        return problem.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
    }

    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task SignInAsync(HttpClient client, string user)
    {
        using var login = await client.GetAsync("/api/v1/auth/login");
        var state = login.Headers.Location!.Query.Split("state=")[1];
        using var callback = await client.GetAsync($"/api/v1/auth/callback?code={user}&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
    }

    private static async Task<RepositoryResponse> SignInAndFindRepositoryAsync(HttpClient client, string user)
    {
        await SignInAsync(client, user);
        return (await client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!.Single(r => r.Name == "private-repo");
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Query);

    /// <summary>Collects every log line the API writes, at every level.</summary>
    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> lines = new();

        public IReadOnlyCollection<string> Lines => lines;

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            lines.Enqueue(state.ToString() ?? string.Empty);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{formatter(state, exception)} {exception}");

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// GitHub, as far as issues need it. "alice" and "bob" are sign-in codes and logins; alice can
    /// reach <c>acme/private-repo</c>. Issue calls are answered only for a token that was asked
    /// for the issues permission.
    /// </summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private const string IssuesToken = "issues-token";
        private const string RepoPath = "/repos/acme/private-repo";
        private static readonly Dictionary<string, long> UserIds = new() { ["alice"] = 1, ["bob"] = 2 };
        private readonly ConcurrentQueue<RecordedRequest> requests = new();

        public IReadOnlyCollection<RecordedRequest> Requests => requests;

        public ConcurrentQueue<JsonDocument> TokenRequests { get; } = new();

        /// <summary>What <c>GET /issues?state=open</c> lists, newest first, pull requests included.</summary>
        public List<Dictionary<string, object?>> OpenIssues { get; } = [];

        public Dictionary<int, Dictionary<string, object?>> Issues { get; } = [];

        public Dictionary<int, object[]> Comments { get; } = [];

        public bool GrantsIssuesPermission { get; set; } = true;

        public bool HasIssues { get; set; } = true;

        /// <summary>Answers a request in place of the stub when it returns a response.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            requests.Enqueue(new RecordedRequest(request.Method, path, query));
            var bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
            var user = bearer.StartsWith("user-token-", StringComparison.Ordinal) ? bearer["user-token-".Length..] : null;

            if (path == "/login/oauth/access_token")
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                var code = form.Split('&').Single(p => p.StartsWith("code=", StringComparison.Ordinal))[5..];
                return Json(new { access_token = UserIds.ContainsKey(code) ? $"user-token-{code}" : null });
            }

            if (user is not null && path == "/user")
            {
                return Json(new { id = UserIds[user], login = user });
            }

            if (user is not null && path == "/user/installations")
            {
                return Json(new { installations = new[] { new { id = InstallationId, account = new { login = "acme" } } } });
            }

            if (user is not null && path == $"/user/installations/{InstallationId}/repositories")
            {
                return Json(new
                {
                    repositories = user == "alice"
                        ? new object[] { new { id = PrivateRepoId, name = "private-repo", owner = new { login = "acme" }, default_branch = "main", @private = true } }
                        : [],
                });
            }

            if (path == $"/app/installations/{InstallationId}/access_tokens" && bearer.Count(c => c == '.') == 2)
            {
                var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                TokenRequests.Enqueue(document);
                var wantsIssues = document.RootElement.GetProperty("permissions").TryGetProperty("issues", out _);
                if (wantsIssues && !GrantsIssuesPermission)
                {
                    return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                    {
                        Content = new StringContent("""{"message":"The permissions requested are not granted to this installation."}"""),
                    };
                }

                return Json(new { token = wantsIssues ? IssuesToken : "contents-token" });
            }

            if (Override?.Invoke(request) is { } overridden)
            {
                return overridden;
            }

            if (!path.StartsWith(RepoPath, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path == RepoPath)
            {
                return Json(new { id = PrivateRepoId, name = "private-repo", has_issues = HasIssues });
            }

            if (bearer != IssuesToken)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"Resource not accessible by integration"}""") };
            }

            if (!HasIssues)
            {
                return new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("""{"message":"Issues are disabled for this repo"}""") };
            }

            var parameters = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
            var segments = path[RepoPath.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (segments)
            {
                case ["issues"]:
                    var perPage = int.Parse(parameters["per_page"]);
                    return Json(OpenIssues.Skip((int.Parse(parameters["page"]) - 1) * perPage).Take(perPage));

                case ["issues", var number] when Issues.TryGetValue(int.Parse(number), out var issue):
                    return Json(issue);

                case ["issues", var number, "comments"] when Comments.TryGetValue(int.Parse(number), out var comments):
                    return Json(comments.Take(int.Parse(parameters["per_page"])));

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") };
            }
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
