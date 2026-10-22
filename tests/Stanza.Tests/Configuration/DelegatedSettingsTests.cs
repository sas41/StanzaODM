using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Configuration;

public sealed class DelegatedSettingsTests
{
    [Theory]
    [InlineData("settings.read", true, false)]
    [InlineData("settings.write", false, false)]
    [InlineData("environment.read", false, false)]
    [InlineData("environment.reveal", true, true)]
    [InlineData("environment.write", true, false)]
    public async Task GlobalEnvironmentReadUseRevealAndWriteAreDistinct(string grant, bool canRead, bool canReveal)
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var actor = await fixture.AccountAsync();
        await fixture.GrantAsync(actor, grant); await using var db = fixture.Context(); var settings = fixture.Settings(db);
        const string secret = "TOKEN=global-secret-sentinel\n";
        await settings.SaveAsync(fixture.Owner, new("https://control.example", secret, 0));
        if (!canRead) await Assert.ThrowsAsync<SettingsOperationException>(() => settings.ReadAsync(actor));
        else
        {
            var visible = await settings.ReadAsync(actor); Assert.Equal("https://control.example", visible.PublicUrl);
            Assert.Equal(canReveal ? secret : "", visible.GlobalEnvironment);
            Assert.DoesNotContain(secret, visible.ToString());
        }
        Assert.False(await settings.CanAsync(actor, "environment.use")); // Not a catalog action; cannot invent an implicit use/reveal grant.
        Assert.Equal(grant == "environment.read", await settings.CanAsync(actor, "environment.read"));
        if (grant != "settings.write") await Assert.ThrowsAsync<SettingsOperationException>(() => settings.SaveEditorAsync(actor, "https://changed.example", null, 1));
        if (grant != "environment.write") await Assert.ThrowsAsync<SettingsOperationException>(() => settings.SaveEditorAsync(actor, null, "NEW=secret", 1));
        Assert.Equal(secret, (await settings.ReadAsync(fixture.Owner)).GlobalEnvironment);
    }

    [Fact]
    public async Task EditingPublicOriginPreservesHiddenEnvironmentAndEnvironmentOnlyWritesPreserveOrigin()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var writer = await fixture.AccountAsync(); var envWriter = await fixture.AccountAsync();
        await fixture.GrantAsync(writer, "settings.read"); await fixture.GrantAsync(writer, "settings.write");
        await fixture.GrantAsync(envWriter, "environment.write");
        await using var db = fixture.Context(); var settings = fixture.Settings(db);
        await settings.SaveAsync(fixture.Owner, new("https://before.example", "SECRET=retained\n", 0));
        Assert.Empty((await settings.ReadAsync(writer)).GlobalEnvironment);
        await settings.SaveEditorAsync(writer, "https://after.example", null, 1);
        Assert.Equal(new InstanceSettings("https://after.example", "SECRET=retained\n", 2), await settings.ReadAsync(fixture.Owner));
        await settings.SaveEditorAsync(envWriter, null, "REPLACED=explicit\n", 2);
        Assert.Equal(new InstanceSettings("https://after.example", "REPLACED=explicit\n", 3), await settings.ReadAsync(fixture.Owner));
        Assert.DoesNotContain("REPLACED=explicit", (await db.Settings.AsNoTracking().SingleAsync(x => x.Key == "instance-settings-v1")).Value);
        Assert.Equal("https://after.example", await settings.ReadPublicOriginAsync());
    }

    [Fact]
    public async Task StaleSettingsVersionsNeverEraseNewerSecrets()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync();
        await using var first = fixture.Context(); await using var second = fixture.Context();
        await fixture.Settings(first).SaveAsync(fixture.Owner, new("https://control.example", "INITIAL=secret", 0));
        var stale = await fixture.Settings(first).ReadAsync(fixture.Owner);
        await fixture.Settings(second).SaveAsync(fixture.Owner, stale with { GlobalEnvironment = "LATEST=secret" });
        await Assert.ThrowsAsync<SettingsOperationException>(() => fixture.Settings(first).SaveAsync(fixture.Owner, stale));
        Assert.Equal("LATEST=secret", (await fixture.Settings(second).ReadAsync(fixture.Owner)).GlobalEnvironment);
    }

    [Fact]
    public async Task MissingAndRevokedGrantsTakeEffectOnAlreadyConstructedServices()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var actor = await fixture.AccountAsync();
        await using var db = fixture.Context(); var service = fixture.Settings(db);
        await Assert.ThrowsAsync<SettingsOperationException>(() => service.ReadAsync(actor));
        await fixture.GrantAsync(actor, "environment.reveal"); Assert.NotNull(await service.ReadAsync(actor));
        await using var revoke = fixture.Context();
        await revoke.Grants.Where(x => x.AccountId == actor).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<SettingsOperationException>(() => service.ReadAsync(actor));
        await fixture.GrantAsync(actor, "environment.reveal");
        await new Stanza.ControlPlane.Security.AccountService(revoke, fixture.Clock).SetEnabledAsync(fixture.Owner, actor, false);
        await Assert.ThrowsAsync<SettingsOperationException>(() => service.ReadAsync(actor));
    }
}
