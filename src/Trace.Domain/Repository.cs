namespace Trace.Domain;

public enum RepositoryVisibility
{
    Public,
    Private,
}

/// <summary>A source repository TRACE can index. Not yet linked to a GitHub installation.</summary>
public sealed class Repository : Entity
{
    public const int MaxNameLength = 100;
    public const int MaxBranchLength = 255;

    private Repository()
    {
    }

    public string Owner { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string DefaultBranch { get; private set; } = null!;

    public RepositoryVisibility Visibility { get; private set; }

    /// <summary>Stable GitHub repository ID; null for locally seeded fixture repositories.</summary>
    public long? GitHubRepoId { get; private set; }

    /// <summary>The GitHub App installation this repository is reached through; null for the local demo fixture.</summary>
    public Guid? InstallationId { get; private set; }

    public static Repository Create(
        string owner,
        string name,
        string defaultBranch,
        RepositoryVisibility visibility,
        long? gitHubRepoId = null) => new()
        {
            Owner = Guard.Required(owner, MaxNameLength, nameof(owner)),
            Name = Guard.Required(name, MaxNameLength, nameof(name)),
            DefaultBranch = Guard.Required(defaultBranch, MaxBranchLength, nameof(defaultBranch)),
            Visibility = visibility,
            GitHubRepoId = gitHubRepoId,
        };

    /// <summary>A repository reached through a GitHub App installation, keyed by GitHub's stable repository ID.</summary>
    public static Repository FromGitHub(
        Guid installationId,
        long gitHubRepoId,
        string owner,
        string name,
        string defaultBranch,
        RepositoryVisibility visibility)
    {
        var repository = Create(owner, name, defaultBranch, visibility, gitHubRepoId);
        repository.InstallationId = installationId;
        return repository;
    }

    /// <summary>Applies GitHub's current view: repositories can be renamed, moved or change visibility.</summary>
    public void UpdateFromGitHub(
        Guid installationId,
        string owner,
        string name,
        string defaultBranch,
        RepositoryVisibility visibility)
    {
        InstallationId = installationId;
        Owner = Guard.Required(owner, MaxNameLength, nameof(owner));
        Name = Guard.Required(name, MaxNameLength, nameof(name));
        DefaultBranch = Guard.Required(defaultBranch, MaxBranchLength, nameof(defaultBranch));
        Visibility = visibility;
    }
}
