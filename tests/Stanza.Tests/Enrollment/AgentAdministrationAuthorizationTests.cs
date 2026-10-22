using Stanza.Core.Contracts;
using Stanza.Core.Security;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Security;

namespace Stanza.Tests.Enrollment;

public sealed class AgentAdministrationAuthorizationTests
{
    [Theory]
    [InlineData(AccountRole.User, true)]
    [InlineData(AccountRole.Admin, false)]
    [InlineData(AccountRole.SystemAdmin, false)]
    public async Task OrdinaryOrDisabledAccountsCannotManageAgentsOrReadEnvironment(AccountRole role, bool enabled)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await AgentEnvironmentTests.RegisterAgentAsync(fixture);
        await using var db = fixture.CreateContext();
        var accounts = new AccountService(db, fixture.Clock);
        var actor = await accounts.CreateAsync(fixture.OwnerId, "actor", "a-secure-test-password-123", role);
        if (!enabled) await accounts.SetEnabledAsync(fixture.OwnerId, actor.Id, false);
        var operations = new Func<AgentAdministrationService, Task>[]
        {
            service => service.ListAsync(actor.Id),
            service => service.IssueTokenAsync(actor.Id, 15),
            service => service.SetStatusAsync(actor.Id, agent.Id, AgentEnrollmentProtocol.Approved),
            service => service.ReadEnvironmentAsync(actor.Id, agent.Id),
            service => service.SaveEnvironmentAsync(actor.Id, agent.Id, new("SECRET=unauthorized", 0))
        };
        foreach (var operation in operations)
            Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(operation))).StatusCode);
        Assert.Equal(AgentEnrollmentProtocol.Pending, Assert.Single(await fixture.ListAsync()).Status);
        Assert.Equal(new AgentEnvironment("", 0), await fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id)));
    }

    [Fact]
    public async Task UnknownActorCannotUseAnyAdministrativeOperation()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await AgentEnvironmentTests.RegisterAgentAsync(fixture);
        foreach (var actorId in new[] { Guid.Empty, Guid.NewGuid() })
        {
            Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service => service.ListAsync(actorId)))).StatusCode);
            Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service => service.IssueTokenAsync(actorId, 1)))).StatusCode);
            Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service => service.SetStatusAsync(actorId, agent.Id, AgentEnrollmentProtocol.Approved)))).StatusCode);
            Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service => service.ReadEnvironmentAsync(actorId, agent.Id)))).StatusCode);
            Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service => service.SaveEnvironmentAsync(actorId, agent.Id, new("", 0))))).StatusCode);
        }
    }

    [Fact]
    public async Task EnabledAdminHasFullAgentManagementAccessWithoutSystemAdminRole()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        await using var db = fixture.CreateContext();
        var admin = await new AccountService(db, fixture.Clock).CreateAsync(fixture.OwnerId, "admin", "a-secure-test-password-123", AccountRole.Admin);
        using var key = new EnrollmentKey();
        var issued = await fixture.RunAsync(service => service.IssueTokenAsync(admin.Id, 1));
        await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()));
        var agent = Assert.Single(await fixture.RunAsync(service => service.ListAsync(admin.Id)));
        await fixture.RunAsync(service => service.SetStatusAsync(admin.Id, agent.Id, AgentEnrollmentProtocol.Approved));
        await fixture.RunAsync(service => service.SaveEnvironmentAsync(admin.Id, agent.Id, new("KEY=exact-value\r\n", 1)));
        Assert.Equal(new AgentEnvironment("KEY=exact-value\r\n", 2), await fixture.RunAsync(service => service.ReadEnvironmentAsync(admin.Id, agent.Id)));
    }

    [Fact]
    public async Task AccountDisableIsCheckedAgainEvenInReusedServiceContext()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        await using var db = fixture.CreateContext();
        var admin = await new AccountService(db, fixture.Clock).CreateAsync(fixture.OwnerId, "admin", "a-secure-test-password-123", AccountRole.Admin);
        var service = fixture.Service(db);
        await service.ListAsync(admin.Id);
        await fixture.RunAsync(async _ =>
        {
            await using var other = fixture.CreateContext();
            await new AccountService(other, fixture.Clock).SetEnabledAsync(fixture.OwnerId, admin.Id, false);
        });
        Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => service.IssueTokenAsync(admin.Id, 1))).StatusCode);
    }
}
