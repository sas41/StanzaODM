using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Web;

public sealed class PostgreSqlModelTests
{
    [Fact]
    public void FreshPostgreSqlSchemaIncludesAccountAndEnrollmentConstraints()
    {
        using var store = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>()
            .UseNpgsql("Host=not-connected;Database=stanza;Username=not-used;Password=not-used").Options);
        var sql = store.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE \"SecurityAccounts\"", sql);
        Assert.Contains("CREATE TABLE \"SecurityGuard\"", sql);
        Assert.Contains("CREATE TABLE \"Agents\"", sql);
        Assert.Contains("CREATE TABLE \"AgentEnrollmentTokens\"", sql);
        Assert.Contains("CREATE TABLE \"AgentEnrollmentNonces\"", sql);
        Assert.Contains("CK_Grant_OneOwner", sql);
        Assert.Contains("CREATE UNIQUE INDEX", sql);
        Assert.DoesNotContain("__EFMigrationsHistory", sql);
    }
}
