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
}
