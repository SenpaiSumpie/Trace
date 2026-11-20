namespace Trace.Domain;

/// <summary>A person who signed in with GitHub. Identified by GitHub's numeric user ID, never by login or email.</summary>
public sealed class User : Entity
{
    public const int MaxLoginLength = 100;

    private User()
    {
    }

    public long GitHubUserId { get; private set; }

    /// <summary>Display only; logins can be renamed and reused.</summary>
    public string Login { get; private set; } = null!;

    public static User Create(long gitHubUserId, string login)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gitHubUserId);
        return new User { GitHubUserId = gitHubUserId, Login = Guard.Required(login, MaxLoginLength, nameof(login)) };
    }

    public void Rename(string login) => Login = Guard.Required(login, MaxLoginLength, nameof(login));
}

public enum InstallationStatus
{
    Active,
    Suspended,
}

/// <summary>One installation of the TRACE GitHub App on a user or organization account. No tokens are stored.</summary>
public sealed class GitHubInstallation : Entity
{
    private GitHubInstallation()
    {
    }

    public long InstallationId { get; private set; }

    public string AccountLogin { get; private set; } = null!;

    public InstallationStatus Status { get; private set; }

    public static GitHubInstallation Create(long installationId, string accountLogin)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installationId);
        return new GitHubInstallation
        {
            InstallationId = installationId,
            AccountLogin = Guard.Required(accountLogin, User.MaxLoginLength, nameof(accountLogin)),
            Status = InstallationStatus.Active,
        };
    }

    public void SetStatus(InstallationStatus status) => Status = status;
}

/// <summary>
/// Grants one user access to one repository. Rows are replaced from GitHub's own answer each
/// time the user signs in, and removed when GitHub reports the access gone.
/// </summary>
public sealed class RepositoryAccess : Entity
{
    private RepositoryAccess()
    {
    }

    public Guid UserId { get; private set; }

    public Guid RepositoryId { get; private set; }

    public static RepositoryAccess Grant(Guid userId, Guid repositoryId) =>
        new() { UserId = userId, RepositoryId = repositoryId };
}
