using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Trace.Contracts.V1;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>
/// The history list for callers who are not signed in, with the API running as in local
/// development, where anonymous callers may ask the demo repository. Each client here is one
/// browser with its own cookies. A question is user input and may be personal: one visitor
/// must never be listed another visitor's.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AnonymousHistoryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string HistoryUrl = "/api/v1/investigations";
    private const string CookieName = "trace_visitor";

    private readonly WebApplicationFactory<Program> baseFactory = new();
    private TestDatabase database = null!;
    private WebApplicationFactory<Program> factory = null!;

    public async Task InitializeAsync()
    {
        database = await postgres.CreateDatabaseAsync(migrate: false);
        factory = baseFactory.WithDatabase(database.ConnectionString, initializeOnStartup: true);
    }

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await baseFactory.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task Each_browser_is_listed_only_what_it_asked_and_a_browser_without_the_cookie_nothing()
    {
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();
        using var stranger = factory.CreateClient();
        var demo = Assert.Single((await first.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);

        var firstQuestion = await AskAsync(first, demo, "first visitor's question");
        var secondQuestion = await AskAsync(second, demo, "second visitor's question");
        using var plan = await first.PostAsJsonAsync(
            $"/api/v1/repositories/{demo.Id}/issues/5/plans", new CreatePlanRequest(demo.LatestSnapshot!.Id, null));
        var planned = (await plan.Content.ReadFromJsonAsync<PlanResponse>())!;

        var firstList = await ListAsync(first);
        var secondList = await ListAsync(second);
        using var strangerResponse = await stranger.GetAsync(HistoryUrl);

        Assert.Equal([planned.InvestigationId, firstQuestion.InvestigationId], firstList.Items.Select(item => item.InvestigationId));
        Assert.Equal(planned.PlanId, firstList.Items[0].PlanId);
        Assert.Equal("second visitor's question", Assert.Single(secondList.Items).Question);
        Assert.Equal("""{"items":[],"nextCursor":null}""", await strangerResponse.Content.ReadAsStringAsync());

        // Listing never hands out a cookie, and paging or filtering reaches no further.
        Assert.False(strangerResponse.Headers.Contains(HeaderNames.SetCookie));
        Assert.Empty((await ListAsync(stranger, $"?repositoryId={demo.Id}&limit=100")).Items);
        Assert.Null((await ListAsync(second, "?limit=1")).NextCursor);
        var firstPage = await ListAsync(first, "?limit=1");
        Assert.All((await ListAsync(second, $"?cursor={firstPage.NextCursor}")).Items, item => Assert.Equal(secondQuestion.InvestigationId, item.InvestigationId));

        // The address still works for anyone who may see the demo repository, as before.
        using var strangerReads = await stranger.GetAsync($"{HistoryUrl}/{firstQuestion.InvestigationId}");
        Assert.Equal(HttpStatusCode.OK, strangerReads.StatusCode);
    }

    [Fact]
    public async Task The_cookie_is_a_random_token_only_script_cannot_read_and_only_its_hash_is_stored()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var demo = Assert.Single((await client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);

        using var asked = await client.PostAsJsonAsync(
            HistoryUrl, new CreateInvestigationRequest(demo.Id, demo.LatestSnapshot!.Id, "question", "q?", null));
        var investigation = (await asked.Content.ReadFromJsonAsync<InvestigationResponse>())!;
        var cookie = SetCookieHeaderValue.Parse(Assert.Single(asked.Headers.GetValues(HeaderNames.SetCookie)));

        Assert.Equal(CookieName, cookie.Name.Value);
        Assert.Matches("^[0-9a-f]{64}$", cookie.Value.Value);
        Assert.True(cookie.HttpOnly);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        Assert.Equal("/api/v1", cookie.Path.Value);

        await using var db = database.CreateContext();
        var stored = await db.Investigations.SingleAsync(i => i.Id == investigation.InvestigationId);
        Assert.Null(stored.UserId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(cookie.Value.Value!))), stored.VisitorKey);
        Assert.NotEqual(cookie.Value.Value, stored.VisitorKey);

        // The same browser keeps its cookie: asking again sets none, and both rows are listed.
        using var again = Request(HttpMethod.Post, HistoryUrl, cookie.Value.Value!);
        again.Content = JsonContent.Create(new CreateInvestigationRequest(demo.Id, demo.LatestSnapshot.Id, "question", "q2?", null));
        using var askedAgain = await client.SendAsync(again);
        using var listed = await client.SendAsync(Request(HttpMethod.Get, HistoryUrl, cookie.Value.Value!));
        Assert.False(askedAgain.Headers.Contains(HeaderNames.SetCookie));
        Assert.Equal(2, (await listed.Content.ReadFromJsonAsync<InvestigationListResponse>())!.Items.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task A_cookie_that_is_not_a_token_lists_nothing_and_is_replaced_when_asking(string forged)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var honest = factory.CreateClient();
        var demo = Assert.Single((await client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);
        await AskAsync(honest, demo, "someone else's question");

        using var listed = await client.SendAsync(Request(HttpMethod.Get, HistoryUrl, forged));
        using var ask = Request(HttpMethod.Post, HistoryUrl, forged);
        ask.Content = JsonContent.Create(new CreateInvestigationRequest(demo.Id, demo.LatestSnapshot!.Id, "question", "q?", null));
        using var asked = await client.SendAsync(ask);

        Assert.Equal("""{"items":[],"nextCursor":null}""", await listed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Matches("^[0-9a-f]{64}$", SetCookieHeaderValue.Parse(Assert.Single(asked.Headers.GetValues(HeaderNames.SetCookie))).Value.Value);
    }

    [Fact]
    public async Task Another_visitors_stored_key_is_not_a_cookie()
    {
        using var victim = factory.CreateClient();
        using var attacker = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var demo = Assert.Single((await victim.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);
        var question = await AskAsync(victim, demo, "the victim's question");
        await using var db = database.CreateContext();
        var storedKey = (await db.Investigations.SingleAsync(i => i.Id == question.InvestigationId)).VisitorKey!;

        // Someone who read the database has the hash, which has the shape of a token but is not one.
        using var listed = await attacker.SendAsync(Request(HttpMethod.Get, HistoryUrl, storedKey));

        Assert.Empty((await listed.Content.ReadFromJsonAsync<InvestigationListResponse>())!.Items);
    }

    [Fact]
    public async Task Rows_that_belong_to_nobody_are_listed_for_nobody_but_can_still_be_read_by_id()
    {
        using var client = factory.CreateClient();
        var demo = Assert.Single((await client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);
        var question = await AskAsync(client, demo, "asked before history existed");

        // As a row from before the migration looks: no user and no visitor key.
        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE investigations SET visitor_key = NULL WHERE id = {question.InvestigationId}");
        }

        Assert.Empty((await ListAsync(client)).Items);
        using var read = await client.GetAsync($"{HistoryUrl}/{question.InvestigationId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string visitorCookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(HeaderNames.Cookie, $"{CookieName}={visitorCookie}");
        return request;
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
}
