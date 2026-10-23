using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Security;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Configuration;

public sealed class AgentPolicyTests
{
    [Fact]
    public async Task AgentWithoutOverrideInheritsCurrentOperationalLimitsAndExplicitOverridePersists()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var id = await fixture.AgentAsync();
        await using var db = fixture.Context(); var service = fixture.AgentPolicies(db);
        await fixture.Policies(db).SaveAsync(fixture.Owner, new(new() { OperationTimeoutSeconds = 19 }, 0));
        var inherited = await service.ReadAsync(fixture.Owner, id);
        Assert.Equal(0, inherited.Version); Assert.Equal(19, inherited.Policy.TimeoutSeconds);
        var desired = new AgentRuntimePolicy(false, 11, "unix:///run/custom-docker.sock");
        await service.SaveAsync(fixture.Owner, new(id, desired, 0));
        await using var restarted = fixture.Context();
        Assert.Equal(new AgentPolicyEditor(id, desired with { TimeoutSeconds = 19 }, 1), await fixture.AgentPolicies(restarted).ReadAsync(fixture.Owner, id));
        await fixture.Policies(restarted).SaveAsync(fixture.Owner, new(new() { OperationTimeoutSeconds = 30 }, 1));
        Assert.Equal(desired with { TimeoutSeconds = 30 }, await fixture.AgentPolicies(restarted).RuntimeAsync(id));
    }

    [Theory]
    [InlineData("tcp://docker:2375")]
    [InlineData("https://docker:2376")]
    [InlineData("unix:///")]
    [InlineData("unix:///run/docker.sock?option=unsafe")]
    [InlineData("unix:///run/docker.sock#fragment")]
    [InlineData("unix://user:password@localhost/run/docker.sock")]
    [InlineData("unix:///run/docker\nsock")]
    public async Task RuntimePolicyRejectsRemoteOrAmbiguousDockerEndpoints(string endpoint)
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var id = await fixture.AgentAsync(); await using var db = fixture.Context();
        await Assert.ThrowsAsync<SettingsOperationException>(() => fixture.AgentPolicies(db).SaveAsync(fixture.Owner, new(id, new(DockerEndpoint: endpoint), 0)));
        Assert.False(await db.Set<AgentPolicyEntity>().AnyAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    [InlineData(-1)]
    public async Task AgentSpecificTimeoutCannotOverrideGlobalTimeout(int timeout)
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var id = await fixture.AgentAsync(); await using var db = fixture.Context();
        await fixture.AgentPolicies(db).SaveAsync(fixture.Owner, new(id, new(true, timeout), 0));
        Assert.Equal(3600, (await fixture.AgentPolicies(db).RuntimeAsync(id)).TimeoutSeconds);
    }

    [Fact]
    public async Task ScopedReadAndManageGrantsDoNotGrantOtherAgentsOrSecretReveal()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var first = await fixture.AgentAsync(); var second = await fixture.AgentAsync(); var actor = await fixture.AccountAsync();
        await fixture.GrantAsync(actor, "agent.read", "agent", first); await using var db = fixture.Context(); var policies = fixture.AgentPolicies(db);
        Assert.Equal(first, (await policies.ReadAsync(actor, first)).AgentId);
        await Assert.ThrowsAsync<SettingsOperationException>(() => policies.ReadAsync(actor, second));
        await Assert.ThrowsAsync<SettingsOperationException>(() => policies.SaveAsync(actor, new(first, new(), 0)));
        await Assert.ThrowsAsync<AgentOperationException>(() => fixture.Agents(db).ReadEnvironmentAsync(actor, first));
        await fixture.GrantAsync(actor, "agent.manage", "agent", first);
        await policies.SaveAsync(actor, new(first, new(false), 0));
        await Assert.ThrowsAsync<SettingsOperationException>(() => policies.SaveAsync(actor, new(second, new(), 0)));
        await new AccountService(db, fixture.Clock).SetEnabledAsync(fixture.Owner, actor, false);
        await Assert.ThrowsAsync<SettingsOperationException>(() => policies.ReadAsync(actor, first));
        await Assert.ThrowsAsync<SettingsOperationException>(() => policies.SaveAsync(actor, new(first, new(), 1)));
    }

    [Fact]
    public async Task ConflictingAgentPolicyVersionCannotOverwriteNewerPolicy()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var id = await fixture.AgentAsync(); await using var first = fixture.Context(); await using var second = fixture.Context();
        var stale = await fixture.AgentPolicies(first).ReadAsync(fixture.Owner, id);
        await fixture.AgentPolicies(second).SaveAsync(fixture.Owner, new(id, new(false), 0));
        await Assert.ThrowsAsync<SettingsOperationException>(() => fixture.AgentPolicies(first).SaveAsync(fixture.Owner, stale));
        Assert.False((await fixture.AgentPolicies(second).ReadAsync(fixture.Owner, id)).Policy.ExecutionEnabled);
    }

    [Fact]
    public async Task PreviouslyTrackedAgentPolicyCannotBypassVersionCheckAfterAnotherContextSaves()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var id = await fixture.AgentAsync(); await using var first = fixture.Context(); await using var second = fixture.Context();
        await fixture.AgentPolicies(first).SaveAsync(fixture.Owner, new(id, new(true, 30), 0));
        var stale = await fixture.AgentPolicies(first).ReadAsync(fixture.Owner, id);
        await fixture.AgentPolicies(second).SaveAsync(fixture.Owner, new(id, new(false, 60), 1));
        await Assert.ThrowsAsync<SettingsOperationException>(() => fixture.AgentPolicies(first).SaveAsync(fixture.Owner, stale));
        var latest = await fixture.AgentPolicies(second).ReadAsync(fixture.Owner, id);
        Assert.Equal(2, latest.Version); Assert.False(latest.Policy.ExecutionEnabled); Assert.Equal(3600, latest.Policy.TimeoutSeconds);
    }
}
