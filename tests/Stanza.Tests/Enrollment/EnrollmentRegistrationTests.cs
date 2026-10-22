using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Agents;

namespace Stanza.Tests.Enrollment;

public sealed class EnrollmentRegistrationTests
{
    [Fact]
    public async Task InitialClaimStoresOnlyTokenHashAndBindsPendingIdentity()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync(15);
        Assert.Equal(43, issued.Token.Length);
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds(), issued.ExpiresAt);
        Assert.DoesNotContain(issued.Token, issued.ToString());
        var request = key.Request(issued.Token, fixture.Clock.GetUtcNow());
        var response = await fixture.RegisterAsync(request);
        Assert.Equal(new AgentEnrollmentResponse(1, AgentEnrollmentProtocol.Pending), response);
        await using var db = fixture.CreateContext();
        var token = Assert.Single(await db.Set<EnrollmentTokenEntity>().AsNoTracking().ToListAsync());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issued.Token))), token.Hash);
        Assert.NotEqual(issued.Token, token.Hash);
        Assert.Equal(key.Identity, token.BoundIdentity);
        var agent = Assert.Single(await db.Set<AgentEntity>().AsNoTracking().ToListAsync());
        Assert.Equal(key.Identity, agent.Identity);
        Assert.Equal(key.PublicKey, agent.PublicKey);
        Assert.Equal("test-host", agent.Hostname);
        Assert.Equal(fixture.Clock.GetUtcNow().ToUnixTimeSeconds(), agent.RegisteredAt);
        Assert.Equal(agent.RegisteredAt, agent.LastSeenAt);
        Assert.Equal(0, agent.Version);
        Assert.NotEmpty(agent.ProtectedEnvironment);
        Assert.Equal(new AgentEnvironment("", 0), await fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id)));
        Assert.Single(await db.Set<AgentNonceEntity>().ToListAsync());
    }

    [Fact]
    public async Task BoundIdentityCanPollWithFreshProofAfterInitialTokenExpiry()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()));
        var original = Assert.Single(await fixture.ListAsync());
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        var response = await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow(), "renamed-host"));
        Assert.Equal(AgentEnrollmentProtocol.Pending, response.Status);
        var polled = Assert.Single(await fixture.ListAsync());
        Assert.Equal(original.Id, polled.Id);
        Assert.Equal(original.RegisteredAt, polled.RegisteredAt);
        Assert.Equal(original.Version, polled.Version);
        Assert.Equal("renamed-host", polled.Hostname);
        Assert.Equal(fixture.Clock.GetUtcNow().ToUnixTimeSeconds(), polled.LastSeenAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    public async Task InvalidTokenLifetimeDoesNotPersistCredential(int minutes)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        Assert.Equal(400, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.IssueAsync(minutes))).StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Empty(await db.Set<EnrollmentTokenEntity>().ToListAsync());
    }

    [Fact]
    public async Task UnclaimedTokenCannotBeUsedAtItsExpiryBoundary()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow())))).StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Null((await db.Set<EnrollmentTokenEntity>().SingleAsync()).BoundIdentity);
        Assert.Empty(await db.Set<AgentEntity>().ToListAsync());
        Assert.Empty(await db.Set<AgentNonceEntity>().ToListAsync());
    }

    [Fact]
    public async Task ConsumedCredentialCannotBindAnotherKeyButOriginalKeyCanStillPoll()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var original = new EnrollmentKey();
        using var other = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        await fixture.RegisterAsync(original.Request(issued.Token, fixture.Clock.GetUtcNow()));
        var error = await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(other.Request(issued.Token, fixture.Clock.GetUtcNow())));
        Assert.Equal(403, error.StatusCode);
        Assert.DoesNotContain(issued.Token, error.ToString());
        Assert.Equal(AgentEnrollmentProtocol.Pending, (await fixture.RegisterAsync(original.Request(issued.Token, fixture.Clock.GetUtcNow()))).Status);
        Assert.Equal(original.Identity, Assert.Single(await fixture.ListAsync()).Identity);
    }

    [Fact]
    public async Task ExistingIdentityCannotConsumeAnotherFreshToken()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var first = await fixture.IssueAsync();
        await fixture.RegisterAsync(key.Request(first.Token, fixture.Clock.GetUtcNow()));
        var second = await fixture.IssueAsync();
        Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(key.Request(second.Token, fixture.Clock.GetUtcNow())))).StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Single(await db.Set<EnrollmentTokenEntity>().ToListAsync(), credential => credential.BoundIdentity is not null);
        Assert.Single(await db.Set<EnrollmentTokenEntity>().ToListAsync(), credential => credential.BoundIdentity is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedCredentialFailsEvenWhenPreviouslyBound(bool bindFirst)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        if (bindFirst) await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()));
        await using var db = fixture.CreateContext();
        await db.Set<EnrollmentTokenEntity>().ExecuteUpdateAsync(update => update.SetProperty(entity => entity.Revoked, true));
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow())))).StatusCode);
    }

    [Theory]
    [InlineData("null", 401)]
    [InlineData("token-null", 401)]
    [InlineData("token-short", 401)]
    [InlineData("token-oversized", 401)]
    [InlineData("identity-null", 401)]
    [InlineData("identity-forged", 401)]
    [InlineData("key-null", 401)]
    [InlineData("key-invalid", 401)]
    [InlineData("hostname-null", 401)]
    [InlineData("hostname-control", 401)]
    [InlineData("nonce-null", 401)]
    [InlineData("nonce-invalid", 401)]
    [InlineData("signature-null", 401)]
    [InlineData("signature-invalid", 401)]
    [InlineData("version", 426)]
    [InlineData("protocol", 401)]
    public async Task MalformedRequestsFailClosedWithoutClaimsOrSecretDiagnostics(string malformed, int statusCode)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        AgentEnrollmentRequest? request = key.Request(issued.Token, fixture.Clock.GetUtcNow());
        request = malformed switch
        {
            "null" => null,
            "token-null" => request with { Token = null! },
            "token-short" => request with { Token = "short-secret" },
            "token-oversized" => request with { Token = new string('x', 4097) },
            "identity-null" => request with { AgentId = null! },
            "identity-forged" => request with { AgentId = new string('0', 64) },
            "key-null" => request with { PublicKey = null! },
            "key-invalid" => request with { PublicKey = new string('!', request.PublicKey.Length) },
            "hostname-null" => request with { Hostname = null! },
            "hostname-control" => request with { Hostname = "secret\ncontrol" },
            "nonce-null" => request with { Nonce = null! },
            "nonce-invalid" => request with { Nonce = new string('!', 44) },
            "signature-null" => request with { Signature = null! },
            "signature-invalid" => request with { Signature = new string('!', 88) },
            "version" => request with { ProtocolVersion = 2 },
            "protocol" => request with { Protocol = "other-enrollment-protocol" },
            _ => throw new ArgumentException("Unknown malformed case.")
        };
        var error = await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(request));
        Assert.Equal(statusCode, error.StatusCode);
        Assert.DoesNotContain(issued.Token, error.ToString());
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.OrdinalIgnoreCase);
        await using var db = fixture.CreateContext();
        Assert.Null((await db.Set<EnrollmentTokenEntity>().SingleAsync()).BoundIdentity);
        Assert.Empty(await db.Set<AgentEntity>().ToListAsync());
        Assert.Empty(await db.Set<AgentNonceEntity>().ToListAsync());
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public async Task StaleOrFutureProofCannotClaimToken(int skewSeconds)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(key.Request(issued.Token,
            fixture.Clock.GetUtcNow().AddSeconds(skewSeconds))))).StatusCode);
        Assert.Empty(await fixture.ListAsync());
    }

    [Fact]
    public async Task WrongSigningKeyOrTokenCannotImpersonatePublicIdentity()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var owner = new EnrollmentKey();
        using var attacker = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        var real = owner.Request(issued.Token, fixture.Clock.GetUtcNow());
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(attacker.Sign(real)))).StatusCode);
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(real with { Token = new string('s', 43) }))).StatusCode);
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(owner.Request(new string('u', 43), fixture.Clock.GetUtcNow())))).StatusCode);
        Assert.Empty(await fixture.ListAsync());
    }

    [Fact]
    public async Task MissingSetupGuardDoesNotConsumeCredential()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync(false);
        using var key = new EnrollmentKey();
        Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RegisterAsync(
            key.Request(new string('t', 43), fixture.Clock.GetUtcNow())))).StatusCode);
    }
}
