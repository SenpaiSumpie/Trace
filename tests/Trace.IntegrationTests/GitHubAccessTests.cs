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
using Trace.Contracts.V1;
using Trace.Domain;
using Trace.Infrastructure.GitHub;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>
/// Sign-in, per-user access, GitHub indexing and webhooks, with GitHub's HTTP API replaced by
/// a stub that answers in GitHub's documented formats. The real GitHub is never called.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GitHubAccessTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string WebhookSecret = "webhook-test-secret";
    private const string HeadSha = "1234567890abcdef1234567890abcdef12345678";
    private const long InstallationId = 101;
    private const long PrivateRepoId = 5001;

    private readonly WebApplicationFactory<Program> baseFactory = new();
    private readonly FakeGitHub gitHub = new();
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
                ["GitHub:WebhookSecret"] = WebhookSecret,
                ["GitHub:PrivateKey"] = RSA.Create(2048).ExportRSAPrivateKeyPem(),
                ["Auth:AllowAnonymousWrites"] = "false",
            }));
            builder.ConfigureTestServices(services => services
                .AddHttpClient(GitHubClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => gitHub));
        });
    }

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await baseFactory.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task Anonymous_caller_sees_only_the_demo_repository_and_cannot_start_work()
    {
        using var anonymous = NewClient();
        await SignInAsync(NewClient(), "alice");

        var me = await anonymous.GetFromJsonAsync<MeResponse>("/api/v1/me");
        var repositories = await anonymous.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories");
        var demo = Assert.Single(repositories!);
        using var ask = await anonymous.PostAsJsonAsync(
            "/api/v1/investigations", new CreateInvestigationRequest(demo.Id, demo.LatestSnapshot!.Id, "question", "q?", null));
        using var index = await anonymous.PostAsync($"/api/v1/repositories/{demo.Id}/index-jobs", null);
        using var cancel = await anonymous.PostAsync($"/api/v1/jobs/{Guid.NewGuid()}/cancel", null);

        Assert.Equal((null, true, false), (me!.User, me.SignInAvailable, me.CanStartWork));
        Assert.Equal("sample-repo", demo.Name);
        Assert.All(new[] { ask, index, cancel }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task Signed_in_user_sees_their_github_repositories_and_another_user_cannot_reach_them()
    {
        using var alice = NewClient();
        using var bob = NewClient();
        await SignInAsync(alice, "alice");
        await SignInAsync(bob, "bob");

        var aliceMe = await alice.GetFromJsonAsync<MeResponse>("/api/v1/me");
        var aliceRepos = await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories");
        var bobRepos = await bob.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories");
        var privateRepo = aliceRepos!.Single(r => r.Name == "private-repo");

        Assert.Equal(("alice", true), (aliceMe!.User!.Login, aliceMe.CanStartWork));
        Assert.Equal(["private-repo", "sample-repo"], aliceRepos!.Select(r => r.Name).Order());
        Assert.Equal(["bobs-repo", "sample-repo"], bobRepos!.Select(r => r.Name).Order());

        // Alice indexes and asks; Bob, knowing every ID, still gets 404 everywhere.
        var snapshot = await IndexAsync(alice, privateRepo.Id);
        using var asked = await alice.PostAsJsonAsync(
            "/api/v1/investigations", new CreateInvestigationRequest(privateRepo.Id, snapshot.Id, "question", "Where is the billing code?", null));
        var investigation = await asked.Content.ReadFromJsonAsync<InvestigationResponse>();
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);

        using var bobAsk = await bob.PostAsJsonAsync(
            "/api/v1/investigations", new CreateInvestigationRequest(privateRepo.Id, snapshot.Id, "question", "q?", null));
        using var bobRead = await bob.GetAsync($"/api/v1/investigations/{investigation!.InvestigationId}");
        using var bobEvents = await bob.GetAsync($"/api/v1/investigations/{investigation.InvestigationId}/events");
        using var bobJob = await bob.GetAsync($"/api/v1/jobs/{investigation.JobId}");
        using var bobCancel = await bob.PostAsync($"/api/v1/jobs/{investigation.JobId}/cancel", null);
        using var bobIndex = await bob.PostAsync($"/api/v1/repositories/{privateRepo.Id}/index-jobs", null);
        using var anonymousRead = await NewClient().GetAsync($"/api/v1/investigations/{investigation.InvestigationId}");
        using var aliceRead = await alice.GetAsync($"/api/v1/investigations/{investigation.InvestigationId}");

        Assert.All(
            new[] { bobAsk, bobRead, bobEvents, bobJob, bobCancel, bobIndex, anonymousRead },
            response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal(HttpStatusCode.OK, aliceRead.StatusCode);

        await using var db = database.CreateContext();
        var stored = await db.Investigations.SingleAsync(i => i.Id == investigation.InvestigationId);
        Assert.Equal((await db.Users.SingleAsync(u => u.Login == "alice")).Id, stored.UserId);
    }

    [Fact]
    public async Task Github_repository_is_indexed_at_its_real_commit_with_read_only_scoped_access_and_secrets_left_out()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var privateRepo = (await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!.Single(r => r.Name == "private-repo");

        var snapshot = await IndexAsync(alice, privateRepo.Id);

        Assert.Equal(HeadSha, snapshot.CommitSha);
        await using var db = database.CreateContext();
        var paths = await db.SourceChunks.Where(c => c.SnapshotId == snapshot.Id).Select(c => c.Path).Distinct().ToListAsync();
        Assert.Equal(["src/Billing.cs"], paths);
        Assert.False(await db.SourceChunks.AnyAsync(c => c.Text.Contains("AKIA")));

        // Only the allowlisted, non-symlink files were even downloaded.
        Assert.Equal(["blob-billing", "blob-settings"], gitHub.BlobsFetched.Order());

        // The installation token was requested for this one repository, read-only.
        var tokenRequest = Assert.Single(gitHub.InstallationTokenRequests).RootElement;
        Assert.Equal([PrivateRepoId], tokenRequest.GetProperty("repository_ids").EnumerateArray().Select(id => id.GetInt64()));
        Assert.Equal("read", tokenRequest.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.Equal(["contents", "metadata"], tokenRequest.GetProperty("permissions").EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Access_removed_on_github_disappears_at_the_next_sign_in()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        Assert.Contains((await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!, r => r.Name == "private-repo");

        gitHub.RepositoriesByUser["alice"] = [];
        await SignInAsync(alice, "alice");

        Assert.DoesNotContain((await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!, r => r.Name == "private-repo");
    }

    [Fact]
    public async Task Sign_in_callback_is_refused_unless_it_matches_the_login_this_browser_started()
    {
        using var client = NewClient();
        using var login = await client.GetAsync("/api/v1/auth/login");

        using var forged = await client.GetAsync("/api/v1/auth/callback?code=alice&state=not-the-state");
        using var otherBrowser = await NewClient().GetAsync($"/api/v1/auth/callback?code=alice&state={StateOf(login)}");
        using var replay = await client.GetAsync($"/api/v1/auth/callback?code=alice&state={StateOf(login)}");

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith("https://github.com/login/oauth/authorize?client_id=Iv1.testclient&state=", login.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, otherBrowser.StatusCode);

        // The failed attempt consumed the state cookie, so even the right state no longer works.
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Null((await client.GetFromJsonAsync<MeResponse>("/api/v1/me"))!.User);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");

        using var logout = await alice.PostAsync("/api/v1/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Null((await alice.GetFromJsonAsync<MeResponse>("/api/v1/me"))!.User);
        Assert.Single((await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);
    }

    [Fact]
    public async Task Webhook_with_a_bad_or_missing_signature_changes_nothing()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var body = JsonSerializer.Serialize(new { action = "deleted", installation = new { id = InstallationId } });

        using var unsigned = await SendWebhookAsync("installation", body, signature: null);
        using var wrongSecret = await SendWebhookAsync("installation", body, signature: Sign(body, "another-secret"));
        using var tampered = await SendWebhookAsync("installation", body.Replace("101", "999", StringComparison.Ordinal), signature: Sign(body, WebhookSecret));
        using var noDelivery = await SendWebhookAsync("installation", body, Sign(body, WebhookSecret), deliveryId: null);

        Assert.All(new[] { unsigned, wrongSecret, tampered, noDelivery }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.GitHubInstallations.CountAsync());
        Assert.Contains((await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!, r => r.Name == "private-repo");
    }

    [Fact]
    public async Task Suspending_the_installation_cuts_off_access_immediately_and_unsuspending_restores_it()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var privateRepo = (await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!.Single(r => r.Name == "private-repo");

        using var suspended = await SendWebhookAsync("installation", new { action = "suspend", installation = new { id = InstallationId } });
        var whileSuspended = await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories");
        using var indexWhileSuspended = await alice.PostAsync($"/api/v1/repositories/{privateRepo.Id}/index-jobs", null);

        using var restored = await SendWebhookAsync("installation", new { action = "unsuspend", installation = new { id = InstallationId } });
        var afterwards = await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories");

        Assert.Equal(HttpStatusCode.Accepted, suspended.StatusCode);
        Assert.DoesNotContain(whileSuspended!, r => r.Name == "private-repo");
        Assert.Equal(HttpStatusCode.NotFound, indexWhileSuspended.StatusCode);
        Assert.Contains(afterwards!, r => r.Name == "private-repo");
    }

    [Fact]
    public async Task Removing_a_repository_or_uninstalling_the_app_deletes_everything_indexed_from_it()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var privateRepo = (await alice.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!.Single(r => r.Name == "private-repo");
        await IndexAsync(alice, privateRepo.Id);

        using var removed = await SendWebhookAsync(
            "installation_repositories",
            new { action = "removed", installation = new { id = InstallationId }, repositories_removed = new[] { new { id = PrivateRepoId } } });

        await using var db = database.CreateContext();
        Assert.Equal(HttpStatusCode.Accepted, removed.StatusCode);
        Assert.False(await db.Repositories.AnyAsync(r => r.GitHubRepoId == PrivateRepoId));
        Assert.False(await db.SourceChunks.AnyAsync(c => c.Path == "src/Billing.cs"));
        Assert.Equal(1, await db.IndexSnapshots.CountAsync());

        using var uninstalled = await SendWebhookAsync("installation", new { action = "deleted", installation = new { id = InstallationId } });

        Assert.Equal(HttpStatusCode.Accepted, uninstalled.StatusCode);
        Assert.Equal(0, await db.GitHubInstallations.CountAsync());
        Assert.Equal(["sample-repo"], await db.Repositories.Select(r => r.Name).ToListAsync());
        Assert.Equal(0, await db.RepositoryAccess.CountAsync());
    }

    [Fact]
    public async Task Push_to_the_default_branch_queues_one_index_job_however_often_it_is_delivered()
    {
        using var alice = NewClient();
        await SignInAsync(alice, "alice");
        var push = new { @ref = "refs/heads/main", repository = new { id = PrivateRepoId } };
        var otherBranch = new { @ref = "refs/heads/feature", repository = new { id = PrivateRepoId } };

        using var first = await SendWebhookAsync("push", push, deliveryId: "delivery-1");
        using var redelivered = await SendWebhookAsync("push", push, deliveryId: "delivery-1");
        using var ignored = await SendWebhookAsync("push", otherBranch, deliveryId: "delivery-2");
        using var unknownEvent = await SendWebhookAsync("star", new { action = "created" }, deliveryId: "delivery-3");

        Assert.All(new[] { first, redelivered, ignored, unknownEvent }, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        await using var db = database.CreateContext();
        var job = await db.Jobs.SingleAsync(j => j.Kind == "repository.index");
        Assert.Equal("github-delivery:delivery-1", job.DedupeKey);
    }

    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task SignInAsync(HttpClient client, string user)
    {
        using var login = await client.GetAsync("/api/v1/auth/login");
        using var callback = await client.GetAsync($"/api/v1/auth/callback?code={user}&state={StateOf(login)}");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.ToString());
    }

    private static string StateOf(HttpResponseMessage login) => login.Headers.Location!.Query.Split("state=")[1];

    private static async Task<SnapshotResponse> IndexAsync(HttpClient client, Guid repositoryId)
    {
        using var queued = await client.PostAsync($"/api/v1/repositories/{repositoryId}/index-jobs", null);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var job = await queued.Content.ReadFromJsonAsync<JobResponse>();

        for (var attempt = 0; attempt < 200; attempt++)
        {
            job = await client.GetFromJsonAsync<JobResponse>($"/api/v1/jobs/{job!.Id}");
            if (job!.Status is "succeeded" or "failed" or "cancelled")
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Equal(("succeeded", null), (job!.Status, job.ErrorCode));
        var repositories = await client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories");
        return repositories!.Single(r => r.Id == repositoryId).LatestSnapshot!;
    }

    private Task<HttpResponseMessage> SendWebhookAsync(string eventName, object payload, string? deliveryId = "delivery-0")
    {
        var body = JsonSerializer.Serialize(payload);
        return SendWebhookAsync(eventName, body, Sign(body, WebhookSecret), deliveryId);
    }

    private async Task<HttpResponseMessage> SendWebhookAsync(string eventName, string body, string? signature, string? deliveryId = "delivery-0")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/github/webhooks")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-GitHub-Event", eventName);
        if (deliveryId is not null)
        {
            request.Headers.Add("X-GitHub-Delivery", deliveryId);
        }

        if (signature is not null)
        {
            request.Headers.Add("X-Hub-Signature-256", signature);
        }

        return await NewClient().SendAsync(request);
    }

    private static string Sign(string body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    /// <summary>GitHub, as far as TRACE uses it. "alice" and "bob" are sign-in codes and logins.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private static readonly Dictionary<string, long> UserIds = new() { ["alice"] = 1, ["bob"] = 2 };

        public Dictionary<string, object[]> RepositoriesByUser { get; } = new()
        {
            ["alice"] = [Repo(PrivateRepoId, "private-repo", isPrivate: true)],
            ["bob"] = [Repo(5002, "bobs-repo", isPrivate: false)],
        };

        public List<JsonDocument> InstallationTokenRequests { get; } = [];

        public List<string> BlobsFetched { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
            var user = bearer.StartsWith("user-token-", StringComparison.Ordinal) ? bearer["user-token-".Length..] : null;

            if (path == "/login/oauth/access_token")
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                var code = form.Split('&').Single(p => p.StartsWith("code=", StringComparison.Ordinal))[5..];
                return Json(UserIds.ContainsKey(code) && form.Contains("client_secret=client-secret", StringComparison.Ordinal)
                    ? new { access_token = (string?)$"user-token-{code}" }
                    : new { access_token = (string?)null });
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
                return Json(new { repositories = RepositoriesByUser[user] });
            }

            if (path == $"/app/installations/{InstallationId}/access_tokens" && bearer.Count(c => c == '.') == 2)
            {
                InstallationTokenRequests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
                return Json(new { token = "installation-token" });
            }

            if (bearer == "installation-token" && path == "/repos/acme/private-repo/commits/main")
            {
                return Json(new { sha = HeadSha });
            }

            if (bearer == "installation-token" && path == $"/repos/acme/private-repo/git/trees/{HeadSha}")
            {
                return Json(new
                {
                    truncated = false,
                    tree = new object[]
                    {
                        new { path = "src", mode = "040000", type = "tree", sha = "tree-src" },
                        new { path = "src/Billing.cs", mode = "100644", type = "blob", sha = "blob-billing", size = 60 },
                        new { path = "src/Settings.cs", mode = "100644", type = "blob", sha = "blob-settings", size = 80 },
                        new { path = "src/Link.cs", mode = "120000", type = "blob", sha = "blob-link", size = 10 },
                        new { path = ".env", mode = "100644", type = "blob", sha = "blob-env", size = 30 },
                        new { path = "vendor/lib.cs", mode = "100644", type = "blob", sha = "blob-vendor", size = 30 },
                        new { path = "sub", mode = "160000", type = "commit", sha = "submodule" },
                    },
                });
            }

            if (bearer == "installation-token" && path.StartsWith("/repos/acme/private-repo/git/blobs/", StringComparison.Ordinal))
            {
                var sha = path[(path.LastIndexOf('/') + 1)..];
                BlobsFetched.Add(sha);
                var content = sha == "blob-billing"
                    ? "namespace Acme;\n\npublic class Billing\n{\n    public decimal Total(decimal net) => net * 1.2m;\n}\n"
                    : "public static class Settings\n{\n    public const string Key = \"AKIA" + "IOSFODNN7EXAMPLE\";\n}\n";
                return Json(new { encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static object Repo(long id, string name, bool isPrivate) =>
            new { id, name, owner = new { login = "acme" }, default_branch = "main", @private = isPrivate };

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
