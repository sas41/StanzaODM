using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Security;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Web;

public sealed class InstanceSettingsTests
{
    [Fact]
    public void DiagnosticsDoNotRevealEnvironmentContents()
    {
        Assert.DoesNotContain("private-value", new InstanceSettings("", "SECRET=private-value", 1).ToString());
    }

    [Fact]
    public async Task SettingsAreEncryptedRoundTripExactTextAndRejectLostUpdates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var store = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(connection).Options);
        await store.Database.EnsureCreatedAsync();
        var accounts = new AccountService(store, TimeProvider.System);
        var owner = await accounts.BootstrapAsync("owner", "a-secure-password-123");
        var settings = new InstanceSettingsService(store, new EphemeralDataProtectionProvider());
        var initial = await settings.ReadAsync(owner.Id);
        Assert.Equal(0, initial.Version);
        const string raw = "# secrets\r\nTOKEN='very-sensitive-value'\r\nEMPTY=\r\n";
        await settings.SaveAsync(owner.Id, new("https://stanza.example.com", raw, 0));
        var saved = await settings.ReadAsync(owner.Id);
        Assert.Equal(raw, saved.GlobalEnvironment);
        Assert.Equal(1, saved.Version);
        Assert.DoesNotContain("very-sensitive-value", (await store.Settings.SingleAsync()).Value);
        await Assert.ThrowsAsync<SettingsOperationException>(() => settings.SaveAsync(owner.Id, new("", "overwrite", 0)));
        Assert.Equal(raw, (await settings.ReadAsync(owner.Id)).GlobalEnvironment);
    }

    [Fact]
    public async Task OrdinaryAndDisabledUsersCannotReadOrSaveSettings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var store = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(connection).Options);
        await store.Database.EnsureCreatedAsync();
        var accounts = new AccountService(store, TimeProvider.System);
        var owner = await accounts.BootstrapAsync("owner", "a-secure-password-123");
        var user = await accounts.CreateAsync(owner.Id, "user", "a-secure-password-123", AccountRole.User);
        var admin = await accounts.CreateAsync(owner.Id, "admin", "a-secure-password-123", AccountRole.Admin);
        var settings = new InstanceSettingsService(store, new EphemeralDataProtectionProvider());
        await Assert.ThrowsAsync<SettingsOperationException>(() => settings.ReadAsync(user.Id));
        await Assert.ThrowsAsync<SettingsOperationException>(() => settings.SaveAsync(user.Id, new("", "", 0)));
        await accounts.SetEnabledAsync(owner.Id, admin.Id, false);
        await Assert.ThrowsAsync<SettingsOperationException>(() => settings.ReadAsync(admin.Id));
    }

    [Theory]
    [InlineData("https://stanza.example.com")]
    [InlineData("https://stanza.example.com:8443/")]
    [InlineData("http://localhost:8080")]
    [InlineData("")]
    public void ValidOriginsAreAccepted(string origin) => InstanceSettingsService.Validate(new(origin, "", 0));

    [Theory]
    [InlineData("http://stanza.example.com")]
    [InlineData("https://user:password@stanza.example.com")]
    [InlineData("https://stanza.example.com/path")]
    [InlineData("https://stanza.example.com?token=secret")]
    [InlineData("file:///etc/passwd")]
    public void InvalidOriginsAreRejected(string origin) =>
        Assert.Throws<SettingsOperationException>(() => InstanceSettingsService.Validate(new(origin, "", 0)));

    [Fact]
    public void NullBytesAndOversizedEnvironmentTextAreRejected()
    {
        Assert.Throws<SettingsOperationException>(() => InstanceSettingsService.Validate(new("", "KEY=\0", 0)));
        Assert.Throws<SettingsOperationException>(() => InstanceSettingsService.Validate(new("", new string('x', 1024 * 1024 + 1), 0)));
    }
}
