using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;

namespace Stanza.Tests.Configuration;

public sealed class OperationalPolicyTests
{
    public static IEnumerable<object[]> NumericBounds() => typeof(OperationalPolicy).GetProperties()
        .Where(x => x.GetCustomAttribute<RangeAttribute>() is not null)
        .SelectMany(x => new[] { new object[] { x.Name, Convert.ToInt32(x.GetCustomAttribute<RangeAttribute>()!.Minimum) - 1 }, new object[] { x.Name, Convert.ToInt32(x.GetCustomAttribute<RangeAttribute>()!.Maximum) + 1 } });

    [Theory, MemberData(nameof(NumericBounds))]
    public async Task EveryUiNumericLimitRejectsValuesOutsideItsSafetyBoundsWithoutPersistence(string property, int invalid)
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var db = fixture.Context();
        var policy = new OperationalPolicy(); typeof(OperationalPolicy).GetProperty(property)!.SetValue(policy, invalid);
        await Assert.ThrowsAsync<SettingsOperationException>(() => fixture.Policies(db).SaveAsync(fixture.Owner, new(policy, 0)));
        Assert.Equal(0, (await fixture.Policies(db).GetAsync()).Version);
        Assert.False(await db.Settings.AnyAsync(x => x.Key == "operational-policy"));
    }

    [Fact]
    public async Task EveryNumericSafetyBoundIsInclusiveWhenRelatedLimitsAreConsistent()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var db = fixture.Context(); var service = fixture.Policies(db); long version = 0;
        foreach (var property in typeof(OperationalPolicy).GetProperties())
        {
            var bounds = property.GetCustomAttribute<RangeAttribute>(); if (bounds is null) continue;
            foreach (var bound in new[] { Convert.ToInt32(bounds.Minimum), Convert.ToInt32(bounds.Maximum) })
            {
                var policy = new OperationalPolicy(); property.SetValue(policy, bound);
                policy.MaximumTextBytes = Math.Min(policy.MaximumTextBytes, policy.MaximumWorkspaceBytes);
                policy.MaximumUploadBytes = Math.Min(policy.MaximumUploadBytes, policy.MaximumWorkspaceBytes);
                policy.MaximumConnectionsPerUser = Math.Min(policy.MaximumConnectionsPerUser, policy.MaximumConnections);
                await service.SaveAsync(fixture.Owner, new(policy, version++)); Assert.Equal(new PolicyEditor(policy, version), await service.GetAsync());
            }
        }
    }

    [Theory]
    [InlineData("upload-workspace")]
    [InlineData("text-workspace")]
    [InlineData("connections")]
    public async Task IndependentLimitsCanBeSavedAndTheEffectiveSmallerLimitApplies(string mismatch)
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var db = fixture.Context();
        var policy = new OperationalPolicy();
        if (mismatch == "upload-workspace") policy.MaximumWorkspaceBytes = policy.MaximumUploadBytes - 1;
        if (mismatch == "text-workspace") { policy.MaximumUploadBytes = 1; policy.MaximumWorkspaceBytes = policy.MaximumTextBytes - 1; }
        if (mismatch == "connections") { policy.MaximumConnections = 4; policy.MaximumConnectionsPerUser = 5; }
        await fixture.Policies(db).SaveAsync(fixture.Owner, new(policy, 0));
        Assert.Equal(policy, (await fixture.Policies(db).GetAsync()).Policy);
    }

    [Theory]
    [InlineData(AccountRole.Admin)]
    [InlineData(AccountRole.SystemAdmin)]
    public async Task AdministrativePolicyPersistsEverySettingAndFeedsRuntimeAndEditorAdapters(AccountRole role)
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var actor = await fixture.AccountAsync(role);
        await using var db = fixture.Context(); var service = fixture.Policies(db);
        Assert.Equal(new PolicyEditor(new(), 0), await service.GetAsync());
        var desired = new OperationalPolicy
        {
            SessionHours = 12, LoginAttempts = 7, LoginWindowMinutes = 2, EnrollmentRequestsPerMinute = 11,
            OperationTimeoutSeconds = 42, MaximumParallelOperations = 2,
            MaximumTextBytes = 128, MaximumUploadBytes = 256, MaximumWorkspaceBytes = 512, MaximumSupportingFiles = 3,
            StatusIntervalSeconds = 2, MaximumConnectionsPerUser = 2, MaximumConnections = 8,
            LogRetentionDays = 2, AuditRetentionDays = 3, AllowHostActions = true,
            TrustedProxyAddresses = "127.0.0.1,10.0.0.0/24", TrustedProxyForwardLimit = 2
        };
        await service.SaveAsync(actor, new(desired, 0));
        await using var restarted = fixture.Context();
        Assert.Equal(new PolicyEditor(desired, 1), await fixture.Policies(restarted).ReadAsync(actor));
        var runtime = await fixture.Policies(restarted).RuntimeAsync();
        Assert.True(runtime.ExecutionEnabled); Assert.Equal(42, runtime.TimeoutSeconds);
        var limits = await new DeploymentEditorAdapters(restarted, fixture.Policies(restarted)).GetLimitsAsync();
        Assert.Equal(128, limits.TextBytes); Assert.Equal(256, limits.UploadBytes);
        Assert.Equal(512, limits.WorkspaceBytes); Assert.Equal(3, limits.SupportingEntries);
    }

    [Fact]
    public async Task OrdinarySettingsWriteGrantNeverAuthorizesSecurityPolicyChanges()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var user = await fixture.AccountAsync();
        await fixture.GrantAsync(user, "settings.read"); await fixture.GrantAsync(user, "settings.write");
        await using var db = fixture.Context(); var service = fixture.Policies(db);
        Assert.Equal(0, (await service.ReadAsync(user)).Version);
        await Assert.ThrowsAsync<SettingsOperationException>(() => service.SaveAsync(user, new(new(), 0)));
        await new AccountService(db, fixture.Clock).SetEnabledAsync(fixture.Owner, user, false);
        await Assert.ThrowsAsync<SettingsOperationException>(() => service.ReadAsync(user));
    }

    [Fact]
    public async Task StaleVersionsCannotReplaceTheLastCommittedPolicy()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var first = fixture.Context(); await using var second = fixture.Context();
        var stale = await fixture.Policies(first).GetAsync();
        await fixture.Policies(second).SaveAsync(fixture.Owner, new(new() { SessionHours = 24 }, 0));
        await Assert.ThrowsAsync<SettingsOperationException>(() => fixture.Policies(first).SaveAsync(fixture.Owner, stale));
        Assert.Equal(24, (await fixture.Policies(first).GetAsync()).Policy.SessionHours);
        Assert.Equal(1, (await fixture.Policies(first).GetAsync()).Version);
    }
}
