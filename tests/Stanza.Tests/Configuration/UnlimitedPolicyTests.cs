using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Live;
using Stanza.Core.Contracts;
using Stanza.Core.Deployments;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Security;
using Stanza.Tests.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.IO.Compression;

namespace Stanza.Tests.Configuration;

public sealed class UnlimitedPolicyTests
{
    [Fact]
    public void DefaultsAndUiDescriptionsCoverEveryOperationalSettingExactlyOnce()
    {
        var policy = new OperationalPolicy();
        Assert.Equal(3600, policy.OperationTimeoutSeconds);
        Assert.Equal(128, policy.MaximumSupportingFiles);
        Assert.Equal(4 * 1024 * 1024, policy.MaximumTextBytes);
        Assert.Equal(16 * 1024 * 1024, policy.MaximumUploadBytes);
        Assert.Equal(64 * 1024 * 1024, policy.MaximumWorkspaceBytes);
        Assert.Equal(typeof(OperationalPolicy).GetProperties().Select(x => x.Name).Order(), PolicyFields.Groups.SelectMany(x => x.Fields).Select(x => x.Name).Order());
        Assert.All(PolicyFields.Groups.SelectMany(x => x.Fields), x => Assert.False(string.IsNullOrWhiteSpace(x.Help)));
        Assert.Null(typeof(OperationalPolicy).GetProperty("MaximumOutputCharacters"));
    }

    [Fact]
    public async Task EveryNumericOperationalSettingAcceptsAndPersistsZeroTogether()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync();
        await using var db = fixture.Context();
        var policy = new OperationalPolicy();
        foreach (var property in typeof(OperationalPolicy).GetProperties().Where(x => x.GetCustomAttribute<RangeAttribute>() is not null)) property.SetValue(policy, 0);
        await fixture.Policies(db).SaveAsync(fixture.Owner, new(policy, 0));
        Assert.Equal(policy, (await fixture.Policies(db).GetAsync()).Policy);
        Assert.Equal(0, (await fixture.Policies(db).RuntimeAsync()).TimeoutSeconds);
    }

    [Fact]
    public void ZeroRemovesFileAndConnectionLimitsButDoesNotRemovePathValidation()
    {
        var files = Enumerable.Range(0, 150).Select(i => ManagedFileSnapshot.Text($"file-{i}", new string('x', 100))).ToArray();
        DeploymentWorkspaceValidator.Validate(files, [], new(0, 0, 0));
        Assert.Throws<ArgumentException>(() => DeploymentWorkspaceValidator.Validate([files[0], files[0]], [], new(0, 0, 0)));
        var leases = new LiveConnectionLeases(0, 0);
        var account = Guid.NewGuid();
        var active = Enumerable.Range(0, 1100).Select(_ => leases.TryAcquire(account)).ToArray();
        Assert.All(active, Assert.NotNull);
        foreach (var lease in active) lease!.Dispose();
    }

    [Theory]
    [InlineData(1000000, 0, false)]
    [InlineData(10, 10, false)]
    [InlineData(11, 10, true)]
    public void LimitChecksTreatZeroAsUnbounded(long value, long limit, bool exceeded) => Assert.Equal(exceeded, LimitPolicy.Exceeded(value, limit));

    [Fact]
    public async Task ZeroFileSettingsApplyThroughDraftsAndArchives()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var db = fixture.Context();
        await fixture.Policies(db).SaveAsync(fixture.Owner, new(new() { MaximumTextBytes = 0, MaximumUploadBytes = 0, MaximumWorkspaceBytes = 0, MaximumSupportingFiles = 0 }, 0));
        var drafts = new DeploymentDraftService(db, fixture.Protection, new AccountService(db, fixture.Clock), fixture.Access(db), fixture.Clock,
            limitsProvider: new DeploymentEditorAdapters(db, fixture.Policies(db)));
        var text = new string('x', 4 * 1024 * 1024 + 1);
        var draft = await drafts.CreateAsync(fixture.Owner, "Unbounded", null, text, "");
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            for (var i = 0; i < 150; i++) { using var entry = zip.CreateEntry($"file-{i}.txt").Open(); entry.WriteByte((byte)'x'); }
        await drafts.ImportArchiveAsync(fixture.Owner, draft.Id, 1, "", memory.ToArray());
        var saved = await drafts.ReadAsync(fixture.Owner, draft.Id);
        Assert.Equal(150, saved.Files.Count);
        Assert.Equal(text, saved.ComposeText);
    }

    [Fact]
    public async Task BrowserCanSaveAllZeroValuesAndSessionHasNoScheduledExpiry()
    {
        var clock = new AdjustableClock();
        await using var factory = new StanzaFactory(clock); await factory.BootstrapAsync();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await ConfigurationHttp.LoginAsync(client, "owner");
        var policy = new OperationalPolicy();
        foreach (var property in typeof(OperationalPolicy).GetProperties().Where(x => x.PropertyType == typeof(int))) property.SetValue(policy, 0);
        var token = await ConfigurationHttp.TokenAsync(client, "/settings");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/settings/operations", ConfigurationHttp.Policy(policy, 0, token))).StatusCode);
        token = await ConfigurationHttp.TokenAsync(client, "/settings");
        await client.PostAsync("/account/logout", ConfigurationHttp.Form(("__RequestVerificationToken", token)));
        await ConfigurationHttp.LoginAsync(client, "owner");
        clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/settings")).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(policy, (await scope.ServiceProvider.GetRequiredService<OperationalPolicyService>().GetAsync()).Policy);
    }

    [Theory]
    [InlineData(0, 0, HttpStatusCode.OK)]
    [InlineData(1, 0, HttpStatusCode.TooManyRequests)]
    public async Task ZeroLoginLimitDisablesThrottlingAndZeroWindowDoesNotCreateAResetLoop(int attempts, int window, HttpStatusCode expected)
    {
        await using var factory = new StanzaFactory(); var root = await factory.BootstrapAsync();
        await ConfigurationHttp.SavePolicyAsync(factory, root, new() { LoginAttempts = attempts, LoginWindowMinutes = window }, 0);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await ConfigurationHttp.TokenAsync(client, "/account/login");
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsync("/account/login", ConfigurationHttp.Form(("__RequestVerificationToken", token), ("Username", "missing"), ("Password", "wrong-password")));
            Assert.Equal(i == 0 ? HttpStatusCode.OK : expected, response.StatusCode);
        }
    }
}
