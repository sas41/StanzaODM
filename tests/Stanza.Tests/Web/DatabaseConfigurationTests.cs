using Microsoft.Extensions.Configuration;
using Npgsql;
using Stanza.ControlPlane.Configuration;

namespace Stanza.Tests.Web;

public sealed class DatabaseConfigurationTests
{
    [Fact]
    public void ConnectionStringIsAcceptedAndParsed()
    {
        var configuration = Configuration(new() { ["ConnectionStrings:Stanza"] = "Host=localhost;Database=stanza;Username=test;Password=secret" });
        var parsed = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.GetConnectionString(configuration));
        Assert.Equal("localhost", parsed.Host);
        Assert.Equal("stanza", parsed.Database);
    }

    [Theory]
    [InlineData("Host=localhost")]
    [InlineData("Database=stanza")]
    public void IncompleteConnectionsAreRejected(string connection)
    {
        var configuration = Configuration(new() { ["ConnectionStrings:Stanza"] = connection });
        Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.GetConnectionString(configuration));
    }

    [Fact]
    public void MissingBootstrapConfigurationHasActionableError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.GetConnectionString(Configuration(new())));
        Assert.Contains("ConnectionStrings__Stanza", error.Message);
    }

    [Fact]
    public void PasswordFileSupportsGeneratedCredentialWithoutLeakingIt()
    {
        var file = Path.Combine(Path.GetTempPath(), $"stanza-password-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(file, "test-generated-password-with-entropy\n");
            var configuration = Configuration(new() { ["Database:PasswordFile"] = file });
            var parsed = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.GetConnectionString(configuration));
            Assert.Equal("test-generated-password-with-entropy", parsed.Password);
            Assert.False(parsed.IncludeErrorDetail);
            File.WriteAllText(file, "short");
            var error = Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.GetConnectionString(configuration));
            Assert.DoesNotContain("short", error.Message);
        }
        finally { File.Delete(file); }
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
