using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trace.Contracts.V1;

namespace Trace.IntegrationTests;

/// <summary>
/// The history list (<c>GET /api/v1/investigations</c>) with two signed-in users and an
/// anonymous caller, on the same stub of GitHub as the rest of this class: who is listed what,
/// and that paging, filters and cursors cannot be used to reach another user's rows.
/// </summary>
public sealed partial class GitHubAccessTests
{
    private const string HistoryUrl = "/api/v1/investigations";

    [Fact]
    public async Task History_lists_each_user_only_their_own_investigations_and_an_anonymous_caller_none()
    {
        using var alice = NewClient();
        using var bob = NewClient();
        using var anonymous = NewClient();
        await SignInAsync(alice, "alice");
        await SignInAsync(bob, "bob");
        var demo = (await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!.Single(r => r.Name == "sample-repo");

        // Both ask the demo repository, which both can see: sharing a repository does not share history.
        var aliceFirst = await AskAsync(alice, demo, "alice asks first");
        var bobOnly = await AskAsync(bob, demo, "bob asks");
        var aliceSecond = await AskAsync(alice, demo, "alice asks again");

        var aliceList = await ListAsync(alice);
        var bobList = await ListAsync(bob);
        using var anonymousResponse = await anonymous.GetAsync(HistoryUrl);

        // Newest first.
        Assert.Equal([aliceSecond.InvestigationId, aliceFirst.InvestigationId], aliceList.Items.Select(item => item.InvestigationId));
        Assert.Equal(["alice asks again", "alice asks first"], aliceList.Items.Select(item => item.Question));
        Assert.Equal(bobOnly.InvestigationId, Assert.Single(bobList.Items).InvestigationId);
        Assert.Null(aliceList.NextCursor);
        Assert.Null(bobList.NextCursor);

        // An anonymous caller is not listed other people's questions on the demo repository,
        // and the empty answer has the same shape as any other page.
        Assert.Equal(HttpStatusCode.OK, anonymousResponse.StatusCode);
        Assert.Equal("""{"items":[],"nextCursor":null}""", await anonymousResponse.Content.ReadAsStringAsync());
        Assert.False(anonymousResponse.Headers.Contains("Set-Cookie"));

        // Reading by ID is a different rule and did not change: whoever may see the repository may read it.
        using var bobReadsAlice = await bob.GetAsync($"{HistoryUrl}/{aliceFirst.InvestigationId}");
        using var anonymousReadsAlice = await anonymous.GetAsync($"{HistoryUrl}/{aliceFirst.InvestigationId}");
        Assert.Equal(HttpStatusCode.OK, bobReadsAlice.StatusCode);
        Assert.Equal(HttpStatusCode.OK, anonymousReadsAlice.StatusCode);
    }

    [Fact]
    public async Task History_row_carries_what_a_list_needs_and_nothing_of_the_result()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var demo = (await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!.Single(r => r.Name == "sample-repo");

        var question = await AskAsync(alice, demo, "Why does the first page of the task list skip the first results?");
        await WaitForInvestigationAsync(alice, question.InvestigationId);
        using var planned = await alice.PostAsJsonAsync(
            $"/api/v1/repositories/{demo.Id}/issues/5/plans", new CreatePlanRequest(demo.LatestSnapshot!.Id, null));
        var plan = (await planned.Content.ReadFromJsonAsync<PlanResponse>())!;
        await WaitForInvestigationAsync(alice, plan.InvestigationId);

        using var response = await alice.GetAsync(HistoryUrl);
        var raw = await response.Content.ReadAsStringAsync();
        var list = JsonSerializer.Deserialize<InvestigationListResponse>(raw, JsonSerializerOptions.Web)!;

        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(2, list.Items.Count);

        var planRow = list.Items[0];
        Assert.Equal((plan.InvestigationId, "issue_to_plan", "completed", "planned"), (planRow.InvestigationId, planRow.Type, planRow.State, planRow.Outcome));
        Assert.Equal(plan.PlanId, planRow.PlanId);
        Assert.Equal(5, planRow.Issue!.Number);
        Assert.Equal("Requests to paths that merely contain /health skip the API key check", planRow.Issue.Title);
        Assert.Null(planRow.Question);
        Assert.Equal((demo.Id, "trace-demo", "sample-repo"), (planRow.Repository.Id, planRow.Repository.Owner, planRow.Repository.Name));

        var questionRow = list.Items[1];
        Assert.Equal((question.InvestigationId, "question", "completed", "answered"), (questionRow.InvestigationId, questionRow.Type, questionRow.State, questionRow.Outcome));
        Assert.Equal("Why does the first page of the task list skip the first results?", questionRow.Question);
        Assert.Null(questionRow.Issue);
        Assert.Null(questionRow.PlanId);
        Assert.Equal(question.CreatedAt, questionRow.CreatedAt);

        // Exactly these members: no answer, evidence, plan document, tokens or model.
        using var document = JsonDocument.Parse(raw);
        Assert.All(document.RootElement.GetProperty("items").EnumerateArray(), item => Assert.Equal(
            ["createdAt", "investigationId", "issue", "outcome", "planId", "question", "repository", "state", "type"],
            item.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal)));
        Assert.DoesNotContain("Extractive test model", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskRepository.cs", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_pages_by_cursor_in_a_stable_order_and_another_user_cannot_page_filter_or_cursor_into_it()
    {
        using var alice = NewClient();
        using var bob = NewClient();
        await SignInAsync(alice, "alice");
        await SignInAsync(bob, "bob");
        var aliceRepositories = await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories");
        var demo = aliceRepositories!.Single(r => r.Name == "sample-repo");
        var privateRepo = aliceRepositories!.Single(r => r.Name == "private-repo");
        var privateSnapshot = await IndexAsync(alice, privateRepo.Id);

        // Bob's two rows are the oldest and the newest, with Alice's five between them.
        var bobOldest = await AskAsync(bob, demo, "bob 1");
        var aliceIds = new List<Guid>();
        for (var number = 1; number <= 4; number++)
        {
            aliceIds.Add((await AskAsync(alice, demo, $"alice {number}")).InvestigationId);
        }

        using var privateAsked = await alice.PostAsJsonAsync(
            HistoryUrl, new CreateInvestigationRequest(privateRepo.Id, privateSnapshot.Id, "question", "alice private", null));
        aliceIds.Add((await privateAsked.Content.ReadFromJsonAsync<InvestigationResponse>())!.InvestigationId);
        var bobNewest = await AskAsync(bob, demo, "bob 2");

        // Several rows are given the same creation time, so the order is decided by the ID.
        await using (var db = database.CreateContext())
        {
            var sameInstant = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
            var tied = aliceIds.Take(3).Append(bobOldest.InvestigationId).ToList();
            await db.Investigations.Where(i => tied.Contains(i.Id)).ExecuteUpdateAsync(update => update.SetProperty(i => i.CreatedAt, sameInstant));
        }

        // Alice walks her five rows two at a time: every row once, newest first, IDs breaking the tie.
        var walked = new List<InvestigationSummaryResponse>();
        var cursors = new List<string>();
        string? cursor = null;
        do
        {
            var page = await ListAsync(alice, $"?limit=2{(cursor is null ? string.Empty : $"&cursor={cursor}")}");
            Assert.InRange(page.Items.Count, 1, 2);
            walked.AddRange(page.Items);
            cursor = page.NextCursor;
            if (cursor is not null)
            {
                cursors.Add(cursor);
            }
        }
        while (cursor is not null);

        Assert.Equal(
            walked.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.InvestigationId, Comparer<Guid>.Create(CompareAsPostgres)).Select(item => item.InvestigationId),
            walked.Select(item => item.InvestigationId));
        Assert.Equal(aliceIds.Order(), walked.Select(item => item.InvestigationId).Order());
        Assert.Equal(2, cursors.Count);

        // Bob pages one at a time. His second page exists only because he has a second row:
        // Alice's five rows between his two neither fill a page nor produce a cursor.
        var bobFirst = await ListAsync(bob, "?limit=1");
        var bobSecond = await ListAsync(bob, $"?limit=1&cursor={bobFirst.NextCursor}");
        Assert.Equal(bobNewest.InvestigationId, Assert.Single(bobFirst.Items).InvestigationId);
        Assert.NotNull(bobFirst.NextCursor);
        Assert.Equal(bobOldest.InvestigationId, Assert.Single(bobSecond.Items).InvestigationId);
        Assert.Null(bobSecond.NextCursor);
        var bobAll = await ListAsync(bob, "?limit=100");
        Assert.Equal(2, bobAll.Items.Count);
        Assert.Null(bobAll.NextCursor);

        // Alice's cursors, used by Bob, are positions in Bob's own rows and nothing more.
        foreach (var stolen in cursors)
        {
            var page = await ListAsync(bob, $"?cursor={stolen}");
            Assert.All(page.Items, item => Assert.Contains(item.InvestigationId, new[] { bobOldest.InvestigationId, bobNewest.InvestigationId }));
        }

        // Filtering by a repository Bob cannot see is the same 404 as a repository that does not exist.
        using var filteredToAlice = await bob.GetAsync($"{HistoryUrl}?repositoryId={privateRepo.Id}");
        using var filteredToNothing = await bob.GetAsync($"{HistoryUrl}?repositoryId={Guid.NewGuid()}");
        using var anonymousFiltered = await NewClient().GetAsync($"{HistoryUrl}?repositoryId={privateRepo.Id}");
        Assert.All(new[] { filteredToAlice, filteredToNothing, anonymousFiltered }, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal(await ProblemWithoutTraceIdAsync(filteredToNothing), await ProblemWithoutTraceIdAsync(filteredToAlice));

        // The filters narrow Alice's own list.
        var alicePrivate = await ListAsync(alice, $"?repositoryId={privateRepo.Id}");
        var aliceDemo = await ListAsync(alice, $"?repositoryId={demo.Id}&type=question");
        var alicePlans = await ListAsync(alice, "?type=issue_to_plan");
        Assert.Equal("alice private", Assert.Single(alicePrivate.Items).Question);
        Assert.Equal(4, aliceDemo.Items.Count);
        Assert.Empty(alicePlans.Items);
        Assert.Empty((await ListAsync(bob, $"?repositoryId={demo.Id}&type=issue_to_plan")).Items);
    }

    [Fact]
    public async Task History_drops_rows_of_a_repository_the_user_can_no_longer_see()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var repositories = await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories");
        var demo = repositories!.Single(r => r.Name == "sample-repo");
        var privateRepo = repositories!.Single(r => r.Name == "private-repo");
        var snapshot = await IndexAsync(alice, privateRepo.Id);
        var onDemo = await AskAsync(alice, demo, "demo question");
        using var asked = await alice.PostAsJsonAsync(
            HistoryUrl, new CreateInvestigationRequest(privateRepo.Id, snapshot.Id, "question", "private question", null));
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Equal(["private question", "demo question"], (await ListAsync(alice)).Items.Select(item => item.Question));

        // Suspended: gone from the list at once, with no cursor pointing at it, and the filter is a 404.
        using var suspended = await SendWebhookAsync("installation", new { action = "suspend", installation = new { id = InstallationId } });
        var whileSuspended = await ListAsync(alice, "?limit=1");
        using var filteredWhileSuspended = await alice.GetAsync($"{HistoryUrl}?repositoryId={privateRepo.Id}");
        Assert.Equal(onDemo.InvestigationId, Assert.Single(whileSuspended.Items).InvestigationId);
        Assert.Null(whileSuspended.NextCursor);
        Assert.Equal(HttpStatusCode.NotFound, filteredWhileSuspended.StatusCode);

        // Unsuspended: back.
        using var restored = await SendWebhookAsync("installation", new { action = "unsuspend", installation = new { id = InstallationId } });
        Assert.Equal(2, (await ListAsync(alice)).Items.Count);

        // Access removed on GitHub and seen at the next sign-in: gone again, though the row is still stored.
        gitHub.RepositoriesByUser["alice"] = [];
        await SignInAsync(alice, "alice");
        Assert.Equal("demo question", Assert.Single((await ListAsync(alice)).Items).Question);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(2, await db.Investigations.CountAsync());
        }

        // The app is uninstalled: the repository and its investigations are deleted, and the list still answers.
        using var uninstalled = await SendWebhookAsync("installation", new { action = "deleted", installation = new { id = InstallationId } });
        Assert.Equal("demo question", Assert.Single((await ListAsync(alice)).Items).Question);
    }

    [Theory]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=101", "limit")]
    [InlineData("?limit=-1", "limit")]
    [InlineData("?type=plan", "type")]
    [InlineData("?type=QUESTION", "type")]
    [InlineData("?type=", "type")]
    [InlineData("?repositoryId=00000000-0000-0000-0000-000000000000", "repositoryId")]
    public async Task History_refuses_a_bad_limit_type_or_repository_filter(string query, string field)
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");

        using var response = await alice.GetAsync($"{HistoryUrl}{query}");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData("?repositoryId=not-a-guid")]
    [InlineData("?limit=ten")]
    public async Task History_refuses_a_filter_that_is_not_of_its_type(string query)
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");

        using var response = await alice.GetAsync($"{HistoryUrl}{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("AAAA")]
    [InlineData("MQ")]
    [InlineData("aDE6MTowMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDpleHRyYQ")]
    [InlineData("%27%3B%20DROP%20TABLE%20investigations%3B--")]
    public async Task History_refuses_a_cursor_it_did_not_write(string cursor)
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");

        using var response = await alice.GetAsync($"{HistoryUrl}?cursor={cursor}");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_cursor", problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<InvestigationResponse> AskAsync(HttpClient client, RepositoryResponse repository, string question)
    {
        using var response = await client.PostAsJsonAsync(
            HistoryUrl, new CreateInvestigationRequest(repository.Id, repository.LatestSnapshot!.Id, "question", question, null));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InvestigationResponse>())!;
    }

    private static async Task<InvestigationListResponse> ListAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<InvestigationListResponse>($"{HistoryUrl}{query}"))!;

    private static async Task WaitForInvestigationAsync(HttpClient client, Guid investigationId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var investigation = await client.GetFromJsonAsync<InvestigationResponse>($"{HistoryUrl}/{investigationId}");
            if (investigation!.State is "completed" or "failed" or "cancelled")
            {
                Assert.Equal("completed", investigation.State);
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The investigation did not finish.");
    }

    /// <summary>A problem response as text, without the one member that differs for every request.</summary>
    private static async Task<string> ProblemWithoutTraceIdAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return string.Join(
            "; ",
            problem.RootElement.EnumerateObject()
                .Where(member => member.Name != "traceId")
                .Select(member => $"{member.Name}={member.Value.GetRawText()}"));
    }

    /// <summary>PostgreSQL orders uuid values by their bytes as written; .NET's Guid comparison does not.</summary>
    private static int CompareAsPostgres(Guid left, Guid right) =>
        string.CompareOrdinal(left.ToString("N"), right.ToString("N"));
}
