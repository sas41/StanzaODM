using Microsoft.EntityFrameworkCore;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Agents;

namespace Stanza.Tests.Enrollment;

public sealed class EnrollmentLifecycleTests
{
    [Theory]
    [InlineData(AgentEnrollmentProtocol.Approved)]
    [InlineData(AgentEnrollmentProtocol.Rejected)]
    [InlineData(AgentEnrollmentProtocol.Revoked)]
    public async Task AdministrativeDecisionIsAuthoritativeForSubsequentSignedPolls(string status)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()));
        var agent = Assert.Single(await fixture.ListAsync());
        await fixture.SetStatusAsync(agent.Id, status);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(status, (await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()))).Status);
        var after = Assert.Single(await fixture.ListAsync());
        Assert.Equal(status, after.Status);
        Assert.Equal(1, after.Version);
    }

    [Theory]
    [InlineData(AgentEnrollmentProtocol.Approved, AgentEnrollmentProtocol.Rejected)]
    [InlineData(AgentEnrollmentProtocol.Rejected, AgentEnrollmentProtocol.Approved)]
    [InlineData(AgentEnrollmentProtocol.Revoked, AgentEnrollmentProtocol.Approved)]
    [InlineData(AgentEnrollmentProtocol.Revoked, AgentEnrollmentProtocol.Rejected)]
    public async Task NonPendingDecisionCannotBeReversed(string initial, string attempted)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()));
        var agent = Assert.Single(await fixture.ListAsync());
        await fixture.SetStatusAsync(agent.Id, initial);
        Assert.Equal(400, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.SetStatusAsync(agent.Id, attempted))).StatusCode);
        Assert.Equal(initial, Assert.Single(await fixture.ListAsync()).Status);
        var freshToken = await fixture.IssueAsync();
        Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(key.Request(freshToken.Token, fixture.Clock.GetUtcNow())))).StatusCode);
        Assert.Equal(initial, (await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()))).Status);
    }

    [Theory]
    [InlineData(AgentEnrollmentProtocol.Approved)]
    [InlineData(AgentEnrollmentProtocol.Rejected)]
    public async Task RevocationRemainsAvailableAfterApprovalOrRejection(string initial)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()));
        var agent = Assert.Single(await fixture.ListAsync());
        await fixture.SetStatusAsync(agent.Id, initial);
        await fixture.SetStatusAsync(agent.Id, AgentEnrollmentProtocol.Revoked);
        Assert.Equal(AgentEnrollmentProtocol.Revoked, (await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()))).Status);
    }

    [Fact]
    public async Task ReusedContextCannotApproveTrackedPendingAgentAfterAnotherContextRevokesIt()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()));
        await using var db = fixture.CreateContext();
        var stale = await db.Set<AgentEntity>().SingleAsync();
        await fixture.SetStatusAsync(stale.Id, AgentEnrollmentProtocol.Revoked);
        Assert.Equal(400, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.Service(db)
            .SetStatusAsync(fixture.OwnerId, stale.Id, AgentEnrollmentProtocol.Approved))).StatusCode);
        Assert.Equal(AgentEnrollmentProtocol.Revoked, Assert.Single(await fixture.ListAsync()).Status);
    }

    [Fact]
    public async Task ReusedContextReturnsCurrentRevokedStatusAndHonorsCredentialRevocation()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        await using var db = fixture.CreateContext();
        var service = fixture.Service(db);
        await service.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()));
        var tracked = await db.Set<AgentEntity>().SingleAsync();
        await db.Set<EnrollmentTokenEntity>().SingleAsync();
        await fixture.SetStatusAsync(tracked.Id, AgentEnrollmentProtocol.Revoked);
        Assert.Equal(AgentEnrollmentProtocol.Revoked, (await service.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()))).Status);
        await using var other = fixture.CreateContext();
        await other.Set<EnrollmentTokenEntity>().ExecuteUpdateAsync(update => update.SetProperty(entity => entity.Revoked, true));
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => service.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow())))).StatusCode);
    }

    [Fact]
    public async Task MissingBoundIdentityOrChangedPublicKeyDoesNotRecreateAgent()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()));
        await using var db = fixture.CreateContext();
        await db.Set<AgentEntity>().ExecuteUpdateAsync(update => update.SetProperty(entity => entity.PublicKey, "corrupt-key"));
        Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow())))).StatusCode);
        await db.Set<AgentEntity>().ExecuteDeleteAsync();
        Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow())))).StatusCode);
        Assert.Empty(await fixture.ListAsync());
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("unknown")]
    [InlineData(null)]
    public async Task InvalidDecisionsAreControlledErrors(string? status)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        Assert.Equal(400, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.SetStatusAsync(Guid.NewGuid(), status!))).StatusCode);
    }
}
