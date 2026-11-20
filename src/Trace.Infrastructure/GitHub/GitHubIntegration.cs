using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trace.Application.Indexing;
using Trace.Application.Jobs;
using Trace.Domain;
using Trace.Infrastructure.Persistence;

namespace Trace.Infrastructure.GitHub;

/// <summary>
/// Completes "Sign in with GitHub": identifies the user and replaces their repository access
/// with exactly what GitHub says they can reach through the app right now. The user token is
/// used for this and then discarded.
/// </summary>
public sealed class GitHubSignIn(TraceDbContext db, GitHubClient gitHub)
{
    public async Task<User> SignInAsync(string code, CancellationToken cancellationToken = default)
    {
        var userToken = await gitHub.ExchangeCodeAsync(code, cancellationToken);
        var gitHubUser = await gitHub.GetUserAsync(userToken, cancellationToken);
        var accessible = await gitHub.ListAccessibleAsync(userToken, cancellationToken);

        var user = await db.Users.SingleOrDefaultAsync(u => u.GitHubUserId == gitHubUser.Id, cancellationToken);
        if (user is null)
        {
            user = User.Create(gitHubUser.Id, gitHubUser.Login);
            db.Users.Add(user);
        }
        else
        {
            user.Rename(gitHubUser.Login);
        }

        var grantedRepositoryIds = new HashSet<Guid>();
        foreach (var accessibleInstallation in accessible)
        {
            var installation = await db.GitHubInstallations.SingleOrDefaultAsync(
                i => i.InstallationId == accessibleInstallation.InstallationId, cancellationToken);
            if (installation is null)
            {
                installation = GitHubInstallation.Create(accessibleInstallation.InstallationId, accessibleInstallation.AccountLogin);
                db.GitHubInstallations.Add(installation);
            }

            foreach (var repo in accessibleInstallation.Repositories)
            {
                var visibility = repo.IsPrivate ? RepositoryVisibility.Private : RepositoryVisibility.Public;
                var repository = await db.Repositories.SingleOrDefaultAsync(r => r.GitHubRepoId == repo.Id, cancellationToken);
                if (repository is null)
                {
                    repository = Repository.FromGitHub(installation.Id, repo.Id, repo.Owner, repo.Name, repo.DefaultBranch, visibility);
                    db.Repositories.Add(repository);
                }
                else
                {
                    repository.UpdateFromGitHub(installation.Id, repo.Owner, repo.Name, repo.DefaultBranch, visibility);
                }

                grantedRepositoryIds.Add(repository.Id);
            }
        }

        // Replace, never merge: access the user no longer has on GitHub disappears here.
        var existing = await db.RepositoryAccess.Where(a => a.UserId == user.Id).ToListAsync(cancellationToken);
        db.RepositoryAccess.RemoveRange(existing.Where(a => !grantedRepositoryIds.Contains(a.RepositoryId)));
        var alreadyGranted = existing.Select(a => a.RepositoryId).ToHashSet();
        db.RepositoryAccess.AddRange(grantedRepositoryIds
            .Where(id => !alreadyGranted.Contains(id))
            .Select(id => RepositoryAccess.Grant(user.Id, id)));

        await db.SaveChangesAsync(cancellationToken);
        return user;
    }
}

/// <summary>
/// Reads a repository from GitHub at the exact commit its default branch points to, applying
/// the same allowlist, limits and decoding as every other source. Only blobs are read; nothing
/// is cloned or executed.
/// </summary>
public sealed class GitHubRepositorySource(
    GitHubClient gitHub,
    long installationId,
    long gitHubRepoId,
    string owner,
    string name,
    string branch) : IRepositorySource
{
    private const string SymlinkMode = "120000";

    public async Task<SourceRevision> ReadAsync(CancellationToken cancellationToken = default)
    {
        var token = await gitHub.CreateInstallationTokenAsync(installationId, gitHubRepoId, cancellationToken);
        var commitSha = await gitHub.GetBranchHeadAsync(token, owner, name, branch, cancellationToken);
        var tree = await gitHub.GetTreeAsync(token, owner, name, commitSha, cancellationToken)
            ?? throw new SourceLimitExceededException("Repository has too many files for GitHub to list in one response.");

        var candidates = new List<(GitHubTreeEntry Entry, string Language)>();
        foreach (var entry in tree.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            // Blobs only: no submodules ("commit") and no symbolic links.
            if (entry.Type != "blob" || entry.Mode == SymlinkMode)
            {
                continue;
            }

            var segments = entry.Path.Split('/');
            if (segments.Length - 1 > SourceFilePolicy.MaxDirectoryDepth)
            {
                throw new SourceLimitExceededException(
                    $"Repository is nested deeper than {SourceFilePolicy.MaxDirectoryDepth} directories.");
            }

            if (segments[..^1].Any(SourceFilePolicy.IsExcludedDirectory)
                || SourceFilePolicy.LanguageFor(segments[^1]) is not { } language)
            {
                continue;
            }

            if (candidates.Count >= SourceFilePolicy.MaxFiles)
            {
                throw new SourceLimitExceededException(
                    $"Repository has more than {SourceFilePolicy.MaxFiles} indexable files.");
            }

            candidates.Add((entry, language));
        }

        var files = new List<SourceFile>();
        var skipped = new List<SkippedFile>();
        long totalBytes = 0;
        foreach (var (entry, language) in candidates)
        {
            if (entry.Size > SourceFilePolicy.MaxFileBytes)
            {
                skipped.Add(new SkippedFile(entry.Path, SkipReason.TooLarge));
                continue;
            }

            var bytes = await gitHub.GetBlobAsync(token, owner, name, entry.Sha, cancellationToken);
            totalBytes += bytes.Length;
            if (totalBytes > SourceFilePolicy.MaxTotalBytes)
            {
                throw new SourceLimitExceededException(
                    $"Repository exceeds the {SourceFilePolicy.MaxTotalBytes}-byte indexing limit.");
            }

            if (SourceText.TryDecode(bytes, out var text, out var skipReason))
            {
                files.Add(new SourceFile(entry.Path, language, text));
            }
            else
            {
                skipped.Add(new SkippedFile(entry.Path, skipReason));
            }
        }

        return new SourceRevision(commitSha, branch, files, skipped);
    }
}

public enum WebhookOutcome
{
    /// <summary>The signature did not match; nothing was read or changed.</summary>
    Rejected,

    Handled,

    /// <summary>A valid delivery of an event TRACE does not act on.</summary>
    Ignored,
}

/// <summary>
/// Handles GitHub App webhooks. A delivery is trusted only if its HMAC-SHA256 signature matches
/// the shared secret; the payload is not parsed before that. Every action is idempotent, so a
/// redelivered event changes nothing.
/// </summary>
public sealed class GitHubWebhooks(
    TraceDbContext db,
    IJobQueue jobs,
    IJobSignal jobSignal,
    GitHubOptions options,
    TimeProvider timeProvider)
{
    public async Task<WebhookOutcome> HandleAsync(
        string? eventName,
        string? deliveryId,
        string? signatureHeader,
        byte[] body,
        CancellationToken cancellationToken = default)
    {
        if (!HasValidSignature(signatureHeader, body) || string.IsNullOrEmpty(deliveryId))
        {
            return WebhookOutcome.Rejected;
        }

        using var payload = JsonDocument.Parse(body);
        var root = payload.RootElement;
        var action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : null;

        switch (eventName)
        {
            case "installation" when TryGetInstallationId(root, out var installationId):
                return await HandleInstallationAsync(installationId, action, cancellationToken);

            case "installation_repositories" when action == "removed":
                return await RemoveRepositoriesAsync(root, cancellationToken);

            case "push":
                return await HandlePushAsync(root, deliveryId, cancellationToken);

            default:
                return WebhookOutcome.Ignored;
        }
    }

    private bool HasValidSignature(string? signatureHeader, byte[] body)
    {
        const string prefix = "sha256=";
        if (options.WebhookSecret is not { Length: > 0 } secret
            || signatureHeader is null
            || !signatureHeader.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(signatureHeader[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    private async Task<WebhookOutcome> HandleInstallationAsync(long installationId, string? action, CancellationToken cancellationToken)
    {
        var installation = await db.GitHubInstallations.SingleOrDefaultAsync(i => i.InstallationId == installationId, cancellationToken);
        if (installation is null)
        {
            return WebhookOutcome.Ignored;
        }

        switch (action)
        {
            case "deleted":
                // Retention policy: when the app is uninstalled, everything indexed from that
                // installation's repositories is deleted (the cascade covers snapshots, chunks,
                // investigations, evidence and access).
                db.GitHubInstallations.Remove(installation);
                break;
            case "suspend":
                installation.SetStatus(InstallationStatus.Suspended);
                break;
            case "unsuspend":
                installation.SetStatus(InstallationStatus.Active);
                break;
            default:
                return WebhookOutcome.Ignored;
        }

        await db.SaveChangesAsync(cancellationToken);
        return WebhookOutcome.Handled;
    }

    private async Task<WebhookOutcome> RemoveRepositoriesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("repositories_removed", out var removed) || removed.ValueKind != JsonValueKind.Array)
        {
            return WebhookOutcome.Ignored;
        }

        var ids = removed.EnumerateArray()
            .Where(r => r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            .Select(r => (long?)r.GetProperty("id").GetInt64())
            .ToList();

        // Same retention policy as uninstalling: the repository and everything indexed from it go.
        await db.Repositories.Where(r => ids.Contains(r.GitHubRepoId)).ExecuteDeleteAsync(cancellationToken);
        return WebhookOutcome.Handled;
    }

    private async Task<WebhookOutcome> HandlePushAsync(JsonElement root, string deliveryId, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("repository", out var repo)
            || !repo.TryGetProperty("id", out var idElement)
            || !root.TryGetProperty("ref", out var refElement))
        {
            return WebhookOutcome.Ignored;
        }

        var gitHubRepoId = idElement.GetInt64();
        var repository = await db.Repositories.AsNoTracking()
            .SingleOrDefaultAsync(r => r.GitHubRepoId == gitHubRepoId && r.InstallationId != null, cancellationToken);
        if (repository is null || refElement.GetString() != $"refs/heads/{repository.DefaultBranch}")
        {
            return WebhookOutcome.Ignored;
        }

        // The delivery ID is the dedupe key: GitHub redelivering the event queues nothing new.
        await jobs.EnqueueAsync(
            Job.Enqueue(
                IndexJobHandler.JobKind,
                Job.RepositoryScope(repository.Id),
                dedupeKey: $"github-delivery:{deliveryId}",
                IndexJobHandler.Payload(repository.Id),
                timeProvider.GetUtcNow()),
            cancellationToken);
        jobSignal.Notify();
        return WebhookOutcome.Handled;
    }

    private static bool TryGetInstallationId(JsonElement root, out long installationId)
    {
        installationId = 0;
        return root.TryGetProperty("installation", out var installation)
            && installation.TryGetProperty("id", out var id)
            && id.TryGetInt64(out installationId);
    }
}
