using Microsoft.EntityFrameworkCore;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Agents;

namespace Stanza.Tests.Enrollment;

public sealed class EnrollmentReplayTests
{
    [Fact]
    public async Task ReplayIsPersistentlyRejectedAcrossFreshServiceContextsWithoutUpdatingLastSeen()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        var request = key.Request(issued.Token, fixture.Clock.GetUtcNow());
        await fixture.RegisterAsync(request);
        var original = Assert.Single(await fixture.ListAsync());
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(409, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(request))).StatusCode);
        var freshTimestampSameNonce = key.Request(issued.Token, fixture.Clock.GetUtcNow(), "other-host", request.Nonce);
        Assert.Equal(409, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(freshTimestampSameNonce))).StatusCode);
        Assert.Equal(original, Assert.Single(await fixture.ListAsync()));
        await using var db = fixture.CreateContext();
        Assert.Single(await db.Set<AgentNonceEntity>().ToListAsync());
    }

    [Fact]
    public async Task FutureDatedProofRetainsReplayProtectionForItsEntireFreshnessWindow()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        var request = key.Request(issued.Token, fixture.Clock.GetUtcNow().AddMinutes(5));
        await fixture.RegisterAsync(request);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(AgentEnrollmentProtocol.VerifyProof(request, fixture.Clock.GetUtcNow()));
        Assert.Equal(409, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(request))).StatusCode);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request, fixture.Clock.GetUtcNow()));
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(request))).StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Single(await db.Set<AgentNonceEntity>().ToListAsync());
    }

    [Fact]
    public async Task ExpiredNonceCleanupCannotReviveAnOldProofAndSupportsFreshlyResignedNonce()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        await using var db = fixture.CreateContext();
        var service = fixture.Service(db);
        var original = key.Request(issued.Token, fixture.Clock.GetUtcNow());
        await service.RegisterAsync(original);
        fixture.Clock.Advance(TimeSpan.FromMinutes(12));
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => service.RegisterAsync(original))).StatusCode);
        var fresh = key.Request(issued.Token, fixture.Clock.GetUtcNow());
        await service.RegisterAsync(fresh);
        Assert.DoesNotContain(await db.Set<AgentNonceEntity>().AsNoTracking().ToListAsync(), nonce => nonce.Nonce == original.Nonce);
        await service.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow(), nonce: original.Nonce));
        Assert.Equal(2, await db.Set<AgentNonceEntity>().CountAsync());
    }

    [Fact]
    public async Task ConcurrentInitialClaimsForOneTokenYieldExactlyOneIdentityBinding()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var first = new EnrollmentKey();
        using var second = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        var firstRequest = first.Request(issued.Token, fixture.Clock.GetUtcNow());
        var secondRequest = second.Request(issued.Token, fixture.Clock.GetUtcNow());
        var outcomes = await RaceAsync(() => fixture.RegisterAsync(firstRequest), () => fixture.RegisterAsync(secondRequest));
        Assert.Single(outcomes, status => status == 200);
        Assert.Single(outcomes, status => status == 403);
        await using var db = fixture.CreateContext();
        var agent = Assert.Single(await db.Set<AgentEntity>().ToListAsync());
        Assert.Equal(agent.Identity, (await db.Set<EnrollmentTokenEntity>().SingleAsync()).BoundIdentity);
        Assert.Single(await db.Set<AgentNonceEntity>().ToListAsync());
    }

    [Fact]
    public async Task ConcurrentReplayClaimsYieldExactlyOneAcceptedNonce()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        var request = key.Request(issued.Token, fixture.Clock.GetUtcNow());
        var outcomes = await RaceAsync(() => fixture.RegisterAsync(request), () => fixture.RegisterAsync(request));
        Assert.Single(outcomes, status => status == 200);
        Assert.Single(outcomes, status => status == 409);
        await using var db = fixture.CreateContext();
        Assert.Single(await db.Set<AgentEntity>().ToListAsync());
        Assert.Single(await db.Set<AgentNonceEntity>().ToListAsync());
    }

    internal static async Task<int[]> RaceAsync(params Func<Task>[] actions)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = actions.Select(action => Task.Run(async () =>
        {
            await start.Task;
            try { await action(); return 200; }
            catch (AgentOperationException error) { return error.StatusCode; }
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }
}
