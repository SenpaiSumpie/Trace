using System.Xml.Linq;

namespace Trace.UnitTests;

/// <summary>
/// Enforces the dependency rule from the engineering handoff (Section 3):
/// Domain depends on nothing; Application on Domain/abstractions; Infrastructure implements
/// Application; API and Worker compose. Checked against the project files themselves so a
/// forbidden reference fails the build pipeline even before any type uses it.
/// </summary>
public class ProjectDependencyRuleTests
{
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        ["Trace.Domain"] = [],
        ["Trace.Contracts"] = [],
        ["Trace.Application"] = ["Trace.Domain", "Trace.Contracts"],
        ["Trace.Infrastructure"] = ["Trace.Application", "Trace.Domain", "Trace.Contracts"],
        ["Trace.Api"] = ["Trace.Application", "Trace.Infrastructure", "Trace.Contracts", "Trace.Domain"],
        ["Trace.Worker"] = ["Trace.Application", "Trace.Infrastructure", "Trace.Contracts", "Trace.Domain"],
    };

    public static TheoryData<string> Projects => [.. AllowedReferences.Keys];

    [Theory]
    [MemberData(nameof(Projects))]
    public void Project_references_only_allowed_projects(string project)
    {
        var forbidden = ReadProjectReferences(project).Except(AllowedReferences[project]);

        Assert.Empty(forbidden);
    }

    [Fact]
    public void Every_source_project_has_a_dependency_rule()
    {
        var sourceProjects = Directory.GetDirectories(Path.Combine(RepositoryRoot(), "src"))
            .Select(Path.GetFileName);

        Assert.Equal(AllowedReferences.Keys.Order(), sourceProjects.Order());
    }

    private static IEnumerable<string> ReadProjectReferences(string project)
    {
        var path = Path.Combine(RepositoryRoot(), "src", project, $"{project}.csproj");

        return XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(reference => (string)reference.Attribute("Include")!)
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trace.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Trace.slnx above the test output directory.");
    }
}
