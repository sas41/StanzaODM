using System.Reflection;
using System.Xml.Linq;
using Stanza.Agent;
using Stanza.ControlPlane;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;
using Stanza.Core.Security;

namespace Stanza.Tests.Web;

public sealed class ArchitectureTests
{
    [Fact]
    public void CoreContainsSharedRulesAndContractsWithoutApplicationDependencies()
    {
        var core = typeof(AccountRole).Assembly;
        Assert.Equal("Stanza.Core", core.GetName().Name);
        Assert.Same(core, typeof(AgentEnrollmentProtocol).Assembly);
        var references = core.GetReferencedAssemblies().Select(x => x.Name ?? "").ToArray();
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Npgsql", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Stanza.", StringComparison.Ordinal));
    }

    [Fact]
    public void ControlPlaneOwnsPersistenceAndDoesNotReferenceAgentExecution()
    {
        var controlPlane = typeof(ControlPlaneEntryPoint).Assembly;
        Assert.Same(controlPlane, typeof(StanzaDbContext).Assembly);
        var references = controlPlane.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name == "Stanza.Agent");
        Assert.Contains(references, reference => reference.Name == "Stanza.Core");
    }

    [Fact]
    public void AgentDoesNotReferenceControlPlaneOrDatabaseLibraries()
    {
        var references = typeof(AgentEntryPoint).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? "").ToArray();
        Assert.Contains("Stanza.Core", references);
        Assert.DoesNotContain("Stanza.ControlPlane", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Npgsql", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactlyThreeProductionProjectsExistAndEachApplicationOnlyReferencesCore()
    {
        var root = RepositoryRoot();
        var projects = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        Assert.Equal(new[] { "Stanza.Agent.csproj", "Stanza.ControlPlane.csproj", "Stanza.Core.csproj" },
            projects.Select(Path.GetFileName).OrderBy(name => name).ToArray());
        foreach (var name in new[] { "Stanza.ControlPlane", "Stanza.Agent" })
        {
            var project = XDocument.Load(Path.Combine(root, "src", name, $"{name}.csproj"));
            var reference = Assert.Single(project.Descendants("ProjectReference"));
            Assert.Equal("../Stanza.Core/Stanza.Core.csproj", (string?)reference.Attribute("Include"));
        }
        var core = XDocument.Load(Path.Combine(root, "src/Stanza.Core/Stanza.Core.csproj"));
        Assert.Empty(core.Descendants("ProjectReference"));
        Assert.Empty(core.Descendants("PackageReference"));
        Assert.Empty(core.Descendants("FrameworkReference"));
        var solution = XDocument.Load(Path.Combine(root, "Stanza.slnx"));
        foreach (var project in solution.Descendants("Project"))
            Assert.True(File.Exists(Path.Combine(root, (string)project.Attribute("Path")!)));
    }

    [Fact]
    public void SecurityPersistenceDoesNotExposeMigrations()
    {
        var migrationTypes = typeof(StanzaDbContext).Assembly.GetTypes().Where(type =>
            type.GetCustomAttributes().Any(attribute => attribute.GetType().Name == "MigrationAttribute"));
        Assert.Empty(migrationTypes);
    }

    internal static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory, Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") })
            for (var current = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(start); current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "Stanza.slnx"))) return current.FullName;
        throw new InvalidOperationException("The repository root could not be located.");
    }
}
