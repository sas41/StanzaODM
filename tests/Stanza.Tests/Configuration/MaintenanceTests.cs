using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Configuration;

public sealed class MaintenanceTests
{
    [Fact]
    public void RestoreLeaseIsExclusiveAndFailedRestoreResumesNormalOperation()
    {
        var maintenance = new InstanceMaintenance(); Assert.False(maintenance.Paused); Assert.False(maintenance.RestartRequired);
        using (maintenance.BeginRestore())
        {
            Assert.True(maintenance.Paused); Assert.Throws<InvalidOperationException>(() => maintenance.BeginRestore());
        }
        Assert.False(maintenance.Paused); using var retry = maintenance.BeginRestore(); Assert.True(maintenance.Paused);
    }

    [Fact]
    public void CommittedRestoreCannotBeUnpausedByDisposingItsLease()
    {
        var maintenance = new InstanceMaintenance(); var lease = maintenance.BeginRestore(); maintenance.RequireRestart(); lease.Dispose();
        Assert.True(maintenance.RestartRequired); Assert.True(maintenance.Paused);
        Assert.Throws<InvalidOperationException>(() => maintenance.BeginRestore());
        var restartedProcessState = new InstanceMaintenance(); Assert.False(restartedProcessState.Paused); Assert.False(restartedProcessState.RestartRequired);
    }

    [Fact]
    public void DisposingAnOldLeaseAgainCannotUnpauseANewerRestore()
    {
        var maintenance = new InstanceMaintenance(); var first = maintenance.BeginRestore(); first.Dispose();
        using var second = maintenance.BeginRestore(); first.Dispose(); Assert.True(maintenance.Paused);
        Assert.Throws<InvalidOperationException>(() => maintenance.BeginRestore());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceRefusesNewOperationsBeforeAnyOperationStateIsWritten(bool requireRestart)
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        var maintenance = factory.Services.GetRequiredService<InstanceMaintenance>(); using var lease = maintenance.BeginRestore(); if (requireRestart) maintenance.RequireRestart();
        using var scope = factory.Services.CreateScope(); var operations = scope.ServiceProvider.GetRequiredService<IOperationService>();
        var error = await Assert.ThrowsAsync<OperationException>(() => operations.EnqueueAsync(new(owner, null, "deploy")));
        Assert.Contains("restart", error.Message); Assert.Empty(await scope.ServiceProvider.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().ToListAsync());
    }

    [Fact]
    public async Task SuccessfulActualRestoreBlocksLoginAndDataUntilANewHostLoadsTheRestoredKeys()
    {
        const string passphrase = "offline-recovery-test-passphrase";
        await using var source = new StanzaFactory(); await source.BootstrapAsync(); using var sourceClient = ConfigurationHttpTests.Client(source);
        await ConfigurationHttp.LoginAsync(sourceClient, "owner"); var csrf = await ConfigurationHttp.TokenAsync(sourceClient, "/recovery");
        var download = await sourceClient.PostAsync("/recovery/backup", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Passphrase", passphrase), ("Acknowledge", "true")));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        var archive = await download.Content.ReadAsByteArrayAsync();

        await using var destination = new StanzaFactory(); using var destinationClient = ConfigurationHttpTests.Client(destination);
        var destinationCsrf = await ConfigurationHttp.TokenAsync(destinationClient, "/recovery");
        using var upload = new MultipartFormDataContent
        {
            { new ByteArrayContent(archive), "Archive", "recovery.stanza-recovery" },
            { new StringContent(destinationCsrf), "__RequestVerificationToken" }, { new StringContent(passphrase), "Passphrase" }, { new StringContent("true"), "Acknowledge" }
        };
        var restored = await destinationClient.PostAsync("/recovery/restore", upload);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode); Assert.Contains("Restore completed", await restored.Content.ReadAsStringAsync());
        var maintenance = destination.Services.GetRequiredService<InstanceMaintenance>(); Assert.True(maintenance.Paused); Assert.True(maintenance.RestartRequired);
        foreach (var path in new[] { "/account/login", "/settings", "/", "/health/ready" })
        {
            var denied = await destinationClient.GetAsync(path); Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.Contains("Restart the Control Plane", await denied.Content.ReadAsStringAsync());
        }
        var deniedLogin = await destinationClient.PostAsync("/account/login", ConfigurationHttp.Form(("__RequestVerificationToken", destinationCsrf), ("Username", "owner"), ("Password", ConfigurationTestStore.Password)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, deniedLogin.StatusCode);

        // A distinct host receives a fresh maintenance singleton and DP cache, while using
        // the factory's same database connection and persisted configured key directory.
        await using var restarted = destination.WithWebHostBuilder(_ => { });
        using var restartedClient = restarted.CreateClient(new() { AllowAutoRedirect = false });
        Assert.False(restarted.Services.GetRequiredService<InstanceMaintenance>().RestartRequired);
        await ConfigurationHttp.LoginAsync(restartedClient, "owner");
        Assert.Equal(HttpStatusCode.OK, (await restartedClient.GetAsync("/settings")).StatusCode);
        Assert.True(maintenance.RestartRequired); // No hidden reset of the original process state.
    }
}
