using System.Xml.Linq;

namespace Trace.UnitTests;

/// <summary>
/// Enforces the dependency rule from the engineering handoff (Section 3):
/// Domain depends on nothing; Application on Domain/abstractions; Infrastructure implements
/// Application; API and Worker compose. Checked against the project files themselves so a
/// forbidden reference fails the build pipeline even before any type uses it.
/// <para>
/// Projects under <c>tools/</c> are command-line programs for developers. Like the two hosts
/// they compose the application, so they may reference the layers; but they are not hosts and
/// nothing that ships may depend on them, so no source project may reference a tool, and a
/// tool may not reference a host or another tool.
/// </para>
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

    private static readonly Dictionary<string, string[]> AllowedToolReferences = new()
    {
        ["Trace.Evals"] = ["Trace.Application", "Trace.Infrastructure", "Trace.Domain"],
    };

    public static TheoryData<string> Projects => [.. AllowedReferences.Keys];

    public static TheoryData<string> Tools => [.. AllowedToolReferences.Keys];

    [Theory]
    [MemberData(nameof(Projects))]
    public void Project_references_only_allowed_projects(string project)
    {
        var forbidden = ReadProjectReferences("src", project).Select(reference => reference.Name)
            .Except(AllowedReferences[project]);

        Assert.Empty(forbidden);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Source_project_references_stay_inside_src(string project)
    {
        // By location, not only by name: a tool or test project must never be shipped as a dependency.
        var outside = ReadProjectReferences("src", project).Where(reference => reference.Folder != "src");

        Assert.Empty(outside);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Tool_references_only_the_layers_it_composes(string tool)
    {
        var references = ReadProjectReferences("tools", tool).ToList();

        Assert.Empty(references.Select(reference => reference.Name).Except(AllowedToolReferences[tool]));
        Assert.All(references, reference => Assert.Equal("src", reference.Folder));
    }

    [Fact]
    public void Every_source_project_has_a_dependency_rule()
    {
        var sourceProjects = Directory.GetDirectories(Path.Combine(RepositoryRoot(), "src"))
            .Select(Path.GetFileName);

        Assert.Equal(AllowedReferences.Keys.Order(), sourceProjects.Order());
    }

    [Fact]
    public void Every_tool_has_a_dependency_rule()
    {
        var tools = Directory.GetDirectories(Path.Combine(RepositoryRoot(), "tools"))
            .Select(Path.GetFileName);

        Assert.Equal(AllowedToolReferences.Keys.Order(), tools.Order());
    }

    [Fact]
    public void Tools_are_never_allowed_a_host_or_another_tool()
    {
        string[] notForTools = ["Trace.Api", "Trace.Worker", .. AllowedToolReferences.Keys];

        Assert.All(AllowedToolReferences.Values, allowed => Assert.Empty(allowed.Intersect(notForTools)));
    }

    /// <summary>Each referenced project's name and the top-level folder (<c>src</c>, <c>tools</c>, <c>tests</c>) it lives in.</summary>
    private static IEnumerable<(string Name, string Folder)> ReadProjectReferences(string folder, string project)
    {
        var root = RepositoryRoot();
        var projectDirectory = Path.Combine(root, folder, project);

        return XDocument.Load(Path.Combine(projectDirectory, $"{project}.csproj"))
            .Descendants("ProjectReference")
            .Select(reference => ((string)reference.Attribute("Include")!).Replace('\\', '/'))
            .Select(include => (
                Path.GetFileNameWithoutExtension(include),
                Path.GetRelativePath(root, Path.GetFullPath(include, projectDirectory)).Replace('\\', '/').Split('/')[0]));
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
