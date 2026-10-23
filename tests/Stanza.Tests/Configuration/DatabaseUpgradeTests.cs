using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Configuration;

public sealed class DatabaseUpgradeTests
{
    [Fact]
    public async Task UpgradeAddsOutputStoragePreservesSettingsAndOldResultsAndIsIdempotent()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync();
        await using var db = fixture.Context();
        var existing = new OperationalPolicy { OperationTimeoutSeconds = 17, MaximumTextBytes = 999, MaximumSupportingFiles = 77, TrustedProxyAddresses = "", StatusIntervalSeconds = 23 };
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new PolicyEditor(existing, 4)))!;
        json["Policy"]!.AsObject().Remove("ConfigurationIntervalSeconds");
        json["Policy"]!.AsObject().Remove("StaleStatusSeconds");
        json["Policy"]!["MaximumOutputCharacters"] = 65536;
        db.Settings.Add(new() { Key = "operational-policy", Value = json.ToJsonString() });
        var operation = new OperationEntity { Id = Guid.NewGuid(), Kind = "deploy", State = "Failed", ProtectedResponse = "old-encrypted-response" };
        db.Add(operation); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"OperationOutput\"");
        await DatabaseUpgrader.UpgradeAsync(db);
        await DatabaseUpgrader.UpgradeAsync(db);
        var saved = await fixture.Policies(db).GetAsync();
        Assert.Equal(4, saved.Version);
        Assert.Equal(existing with { ConfigurationIntervalSeconds = 23, StaleStatusSeconds = 23 }, saved.Policy);
        Assert.Equal("old-encrypted-response", (await db.Set<OperationEntity>().SingleAsync()).ProtectedResponse);
        Assert.Empty(await db.Set<OperationOutputEntity>().ToListAsync());
        Assert.Equal("2", (await db.Settings.SingleAsync(x => x.Key == "schema-version")).Value);
    }

    [Fact]
    public async Task ExistingInstallationWithUnsavedPolicyPreservesItsImplicitOldDefaults()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var db = fixture.Context();
        await DatabaseUpgrader.UpgradeAsync(db);
        var saved = (await fixture.Policies(db).GetAsync()).Policy;
        Assert.Equal(300, saved.OperationTimeoutSeconds);
        Assert.Equal(100, saved.MaximumSupportingFiles);
        Assert.Equal(1048576, saved.MaximumTextBytes);
        Assert.Equal("", saved.TrustedProxyAddresses);
    }

    [Fact]
    public async Task FreshUnclaimedInstallationUsesNewDefaultsAfterUpgrade()
    {
        await using var db = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await DatabaseUpgrader.UpgradeAsync(db);
        Assert.False(await db.Settings.AnyAsync(x => x.Key == "operational-policy"));
        Assert.Equal(3600, (await new OperationalPolicyService(db, new(db, TimeProvider.System)).GetAsync()).Policy.OperationTimeoutSeconds);
        Assert.Empty(await db.Set<OperationOutputEntity>().ToListAsync());
    }
}
