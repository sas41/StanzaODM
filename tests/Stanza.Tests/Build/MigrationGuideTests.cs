using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;
using Stanza.Tests.Web;

namespace Stanza.Tests.Build;

public sealed class MigrationGuideTests
{
    [Fact]
    public void GuideNamesTheActualProtocolAndSchemaVersions()
    {
        var guide = File.ReadAllText(Path.Combine(ArchitectureTests.RepositoryRoot(), "MIGRATION.md"));
        Assert.Contains($"channel protocol **{AgentWireMessage.Version}**", guide);
        Assert.Contains($"enrollment protocol **{AgentEnrollmentProtocol.Version}**", guide);
        Assert.Contains($"database schema **{DatabaseUpgrader.CurrentVersion}**", guide);
    }

    [Fact]
    public void ManualProcedurePreservesEvidenceAndDoesNotAdvertiseUnimplementedConversions()
    {
        var guide = File.ReadAllText(Path.Combine(ArchitectureTests.RepositoryRoot(), "MIGRATION.md"));
        foreach (var requirement in new[] { "still-pending redesign", "not yet convert operation workspaces", "original request hash/ID",
            "Do not force-clear `ActiveRunId`", "OpenSSH remains installed", "managed Git-source UI currently accepts HTTPS only",
            "PostgreSQL before the Agent spool disappears", "format-2 jobs", "Do not replace the existing `.env`" })
            Assert.Contains(requirement, guide);
    }
}
