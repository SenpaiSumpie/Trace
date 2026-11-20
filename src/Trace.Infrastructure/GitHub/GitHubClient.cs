using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trace.Infrastructure.GitHub;

public sealed class GitHubOptions
{
    /// <summary>The GitHub App's client ID (used for sign-in and as the app's JWT issuer).</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>The GitHub App's private key in PEM format.</summary>
    public string? PrivateKey { get; set; }

    public string? WebhookSecret { get; set; }

    public Uri ApiBaseUrl { get; set; } = new("https://api.github.com/");

    public Uri WebBaseUrl { get; set; } = new("https://github.com/");

    public bool SignInConfigured => ClientId is { Length: > 0 } && ClientSecret is { Length: > 0 };
}

public sealed record GitHubUser(long Id, string Login);

public sealed record GitHubRepo(long Id, string Owner, string Name, string DefaultBranch, bool IsPrivate);

public sealed record GitHubUserInstallation(long InstallationId, string AccountLogin, IReadOnlyList<GitHubRepo> Repositories);

public sealed record GitHubTreeEntry(string Path, string Mode, string Type, string Sha, long? Size);

public sealed class GitHubApiException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// The few GitHub REST calls TRACE makes. Read-only: sign-in, listing what a user can access
/// through the app, and reading repository content with a short-lived installation token that
/// is requested with read permissions for one repository. No token is ever stored or logged.
/// </summary>
public sealed class GitHubClient(HttpClient http, GitHubOptions options, TimeProvider timeProvider)
{
    public const string HttpClientName = "github";

    private const int PageSize = 100;
    private const int MaxPages = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Exchanges the one-time sign-in code for a user token.</summary>
    public async Task<string> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.WebBaseUrl, "login/oauth/access_token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId ?? string.Empty,
                ["client_secret"] = options.ClientSecret ?? string.Empty,
                ["code"] = code,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var token = await SendAsync<TokenResponse>(request, cancellationToken);
        return token.AccessToken is { Length: > 0 } accessToken
            ? accessToken
            : throw new GitHubApiException("GitHub did not accept the sign-in code.");
    }

    public async Task<GitHubUser> GetUserAsync(string userToken, CancellationToken cancellationToken = default)
    {
        var user = await GetAsync<UserResponse>("user", userToken, cancellationToken);
        return new GitHubUser(user.Id, user.Login ?? string.Empty);
    }

    /// <summary>
    /// The app installations this user can reach and, within each, the repositories this user
    /// can access. GitHub computes the intersection of the installation and the user's rights.
    /// </summary>
    public async Task<IReadOnlyList<GitHubUserInstallation>> ListAccessibleAsync(
        string userToken,
        CancellationToken cancellationToken = default)
    {
        var result = new List<GitHubUserInstallation>();
        var installations = await GetPagedAsync<InstallationsPage, InstallationResponse>(
            "user/installations", userToken, page => page.Installations, cancellationToken);

        foreach (var installation in installations)
        {
            var repositories = await GetPagedAsync<RepositoriesPage, RepositoryResponse>(
                $"user/installations/{installation.Id}/repositories", userToken, page => page.Repositories, cancellationToken);
            result.Add(new GitHubUserInstallation(
                installation.Id,
                installation.Account?.Login ?? string.Empty,
                [.. repositories.Select(r => new GitHubRepo(
                    r.Id, r.Owner?.Login ?? string.Empty, r.Name ?? string.Empty, r.DefaultBranch ?? "main", r.Private))]));
        }

        return result;
    }

    /// <summary>A token valid for about an hour, limited to reading one repository's contents and metadata.</summary>
    public async Task<string> CreateInstallationTokenAsync(
        long installationId,
        long repositoryId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(options.ApiBaseUrl, $"app/installations/{installationId}/access_tokens"))
        {
            Content = JsonContent.Create(
                new
                {
                    repository_ids = new[] { repositoryId },
                    permissions = new { contents = "read", metadata = "read" },
                }),
        };
        Authorize(request, CreateAppJwt());

        var token = await SendAsync<InstallationTokenResponse>(request, cancellationToken);
        return token.Token ?? throw new GitHubApiException("GitHub returned no installation token.");
    }

    public async Task<string> GetBranchHeadAsync(
        string token, string owner, string name, string branch, CancellationToken cancellationToken = default)
    {
        var commit = await GetAsync<CommitResponse>(
            $"repos/{Segment(owner)}/{Segment(name)}/commits/{Segment(branch)}", token, cancellationToken);
        return commit.Sha ?? throw new GitHubApiException("GitHub returned no commit for the branch.");
    }

    /// <summary>The full file tree at a commit, or null if it is too large for GitHub to return in one response.</summary>
    public async Task<IReadOnlyList<GitHubTreeEntry>?> GetTreeAsync(
        string token, string owner, string name, string commitSha, CancellationToken cancellationToken = default)
    {
        var tree = await GetAsync<TreeResponse>(
            $"repos/{Segment(owner)}/{Segment(name)}/git/trees/{Segment(commitSha)}?recursive=1", token, cancellationToken);
        return tree.Truncated
            ? null
            : [.. (tree.Tree ?? []).Select(e => new GitHubTreeEntry(e.Path ?? string.Empty, e.Mode ?? string.Empty, e.Type ?? string.Empty, e.Sha ?? string.Empty, e.Size))];
    }

    public async Task<byte[]> GetBlobAsync(
        string token, string owner, string name, string blobSha, CancellationToken cancellationToken = default)
    {
        var blob = await GetAsync<BlobResponse>(
            $"repos/{Segment(owner)}/{Segment(name)}/git/blobs/{Segment(blobSha)}", token, cancellationToken);
        return blob.Encoding == "base64" && blob.Content is not null
            ? Convert.FromBase64String(blob.Content)
            : throw new GitHubApiException("GitHub returned a blob in an unexpected encoding.");
    }

    /// <summary>A short-lived JWT proving we are the app, signed with its private key (RS256).</summary>
    private string CreateAppJwt()
    {
        if (options.PrivateKey is not { Length: > 0 } pem || options.ClientId is not { Length: > 0 } clientId)
        {
            throw new GitHubApiException("The GitHub App private key and client ID are not configured.");
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        // Issued a minute in the past to tolerate clock drift; GitHub allows at most 10 minutes.
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 540, iss = clientId }));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    private async Task<List<TItem>> GetPagedAsync<TPage, TItem>(
        string path,
        string token,
        Func<TPage, IReadOnlyList<TItem>?> items,
        CancellationToken cancellationToken)
    {
        var all = new List<TItem>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var separator = path.Contains('?') ? '&' : '?';
            var batch = items(await GetAsync<TPage>($"{path}{separator}per_page={PageSize}&page={page}", token, cancellationToken)) ?? [];
            all.AddRange(batch);
            if (batch.Count < PageSize)
            {
                break;
            }
        }

        return all;
    }

    private async Task<T> GetAsync<T>(string path, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.ApiBaseUrl, path));
        Authorize(request, token);
        return await SendAsync<T>(request, cancellationToken);
    }

    private static void Authorize(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("trace-app");
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Neither the URL's token nor the response body goes into the message.
                throw new GitHubApiException($"GitHub returned {(int)response.StatusCode} for {request.Method} {request.RequestUri?.AbsolutePath}.");
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw new GitHubApiException("GitHub returned an empty response.");
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubApiException("GitHub could not be reached.", exception);
        }
        catch (JsonException exception)
        {
            throw new GitHubApiException("GitHub returned a response that could not be read.", exception);
        }
    }

    private static string Segment(string value) => Uri.EscapeDataString(value);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TokenResponse(string? AccessToken);

    private sealed record UserResponse(long Id, string? Login);

    private sealed record AccountResponse(string? Login);

    private sealed record InstallationResponse(long Id, AccountResponse? Account);

    private sealed record InstallationsPage(IReadOnlyList<InstallationResponse>? Installations);

    private sealed record RepositoryResponse(
        long Id, string? Name, AccountResponse? Owner, string? DefaultBranch, bool Private);

    private sealed record RepositoriesPage(IReadOnlyList<RepositoryResponse>? Repositories);

    private sealed record InstallationTokenResponse(string? Token);

    private sealed record CommitResponse(string? Sha);

    private sealed record TreeEntryResponse(string? Path, string? Mode, string? Type, string? Sha, long? Size);

    private sealed record TreeResponse(IReadOnlyList<TreeEntryResponse>? Tree, bool Truncated);

    private sealed record BlobResponse(string? Content, string? Encoding);
}
