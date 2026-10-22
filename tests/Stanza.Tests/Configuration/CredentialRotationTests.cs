using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.Core.Contracts;
using Stanza.Tests.Enrollment;

namespace Stanza.Tests.Configuration;

public sealed class CredentialRotationTests
{
    [Fact]
    public async Task RotationRevokesOldCredentialPreservesIdentityAndRequiresApprovalAgain()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync(); using var key = new EnrollmentKey();
        var old = await fixture.IssueAsync(); await fixture.RegisterAsync(key.Request(old.Token, fixture.Clock.GetUtcNow()));
        var agent = Assert.Single(await fixture.ListAsync()); await fixture.SetStatusAsync(agent.Id, "approved");
        var replacement = await fixture.RunAsync(s => s.RotateCredentialAsync(fixture.OwnerId, agent.Id));
        Assert.DoesNotContain(replacement.Token, replacement.ToString()); Assert.NotEqual(old.Token, replacement.Token);
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds(), replacement.ExpiresAt);
        var rotated = Assert.Single(await fixture.ListAsync()); Assert.Equal(agent.Id, rotated.Id); Assert.Equal(agent.Identity, rotated.Identity);
        Assert.Equal("pending", rotated.Status); Assert.Equal(2, rotated.Version);
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(key.Request(old.Token, fixture.Clock.GetUtcNow())))).StatusCode);
        Assert.Equal("pending", (await fixture.RegisterAsync(key.Request(replacement.Token, fixture.Clock.GetUtcNow()))).Status);
        await fixture.SetStatusAsync(agent.Id, "approved");
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal("approved", (await fixture.RegisterAsync(key.Request(replacement.Token, fixture.Clock.GetUtcNow()))).Status);
        await using var db = fixture.CreateContext();
        var tokens = await db.Set<EnrollmentTokenEntity>().AsNoTracking().ToListAsync();
        var revoked = Assert.Single(tokens, x => x.Revoked); Assert.Null(revoked.BoundIdentity);
        var active = Assert.Single(tokens, x => !x.Revoked); Assert.False(active.FirstClaimPending); Assert.Equal(key.Identity, active.BoundIdentity);
        Assert.All(tokens, row => { Assert.DoesNotContain(old.Token, row.Hash); Assert.DoesNotContain(replacement.Token, row.Hash); });
    }

    [Theory]
    [InlineData(899, true)]
    [InlineData(900, false)]
    [InlineData(901, false)]
    public async Task ReplacementMustBeFirstUsedBeforeExpiryAndSuccessfulFirstUseRemainsBound(int seconds, bool succeeds)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync(); using var key = new EnrollmentKey();
        var old = await fixture.IssueAsync(); await fixture.RegisterAsync(key.Request(old.Token, fixture.Clock.GetUtcNow()));
        var agent = Assert.Single(await fixture.ListAsync()); var token = await fixture.RunAsync(s => s.RotateCredentialAsync(fixture.OwnerId, agent.Id));
        fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
        if (!succeeds)
        {
            Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(key.Request(token.Token, fixture.Clock.GetUtcNow())))).StatusCode);
            await using var db = fixture.CreateContext(); Assert.True((await db.Set<EnrollmentTokenEntity>().SingleAsync(x => !x.Revoked)).FirstClaimPending);
        }
        else
        {
            await fixture.RegisterAsync(key.Request(token.Token, fixture.Clock.GetUtcNow())); fixture.Clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal("pending", (await fixture.RegisterAsync(key.Request(token.Token, fixture.Clock.GetUtcNow()))).Status);
        }
    }

    [Fact]
    public async Task AnotherKeyCannotClaimReplacementOrBurnItsFirstUse()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync(); using var owner = new EnrollmentKey(); using var attacker = new EnrollmentKey();
        var original = await fixture.IssueAsync(); await fixture.RegisterAsync(owner.Request(original.Token, fixture.Clock.GetUtcNow()));
        var id = Assert.Single(await fixture.ListAsync()).Id; var rotated = await fixture.RunAsync(s => s.RotateCredentialAsync(fixture.OwnerId, id));
        Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(attacker.Request(rotated.Token, fixture.Clock.GetUtcNow())))).StatusCode);
        await using (var db = fixture.CreateContext()) Assert.True((await db.Set<EnrollmentTokenEntity>().SingleAsync(x => !x.Revoked)).FirstClaimPending);
        Assert.Equal("pending", (await fixture.RegisterAsync(owner.Request(rotated.Token, fixture.Clock.GetUtcNow()))).Status);
        var rotatedAgain = await fixture.RunAsync(s => s.RotateCredentialAsync(fixture.OwnerId, id));
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(owner.Request(rotated.Token, fixture.Clock.GetUtcNow())))).StatusCode);
        Assert.Equal("pending", (await fixture.RegisterAsync(owner.Request(rotatedAgain.Token, fixture.Clock.GetUtcNow()))).Status);
    }
}
