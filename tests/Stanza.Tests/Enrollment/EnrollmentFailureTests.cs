using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Enrollment;

public sealed class EnrollmentFailureTests
{
    [Fact]
    public async Task PersistenceFailureRollsBackCredentialAgentAndNonceAndDoesNotPoisonReusedContext()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var first = new EnrollmentKey();
        using var second = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        var failure = new FailOneSave();
        await using var db = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>(fixture.Options).AddInterceptors(failure).Options);
        var service = fixture.Service(db);
        var error = await Assert.ThrowsAsync<AgentOperationException>(() => service.RegisterAsync(first.Request(issued.Token, fixture.Clock.GetUtcNow())));
        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain("secret", error.ToString());
        await using (var inspection = fixture.CreateContext())
        {
            Assert.Empty(await inspection.Set<AgentEntity>().ToListAsync());
            Assert.Empty(await inspection.Set<AgentNonceEntity>().ToListAsync());
            Assert.Null((await inspection.Set<EnrollmentTokenEntity>().SingleAsync()).BoundIdentity);
        }
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(AgentEnrollmentProtocol.Pending, (await service.RegisterAsync(second.Request(issued.Token, fixture.Clock.GetUtcNow()))).Status);
        Assert.Equal(second.Identity, Assert.Single(await fixture.ListAsync()).Identity);
        Assert.Equal(403, (await Assert.ThrowsAsync<AgentOperationException>(() => service.RegisterAsync(first.Request(issued.Token, fixture.Clock.GetUtcNow())))).StatusCode);
    }

    [Fact]
    public async Task TokenIssuanceFailureDoesNotLeaveTrackedCredentialForAnotherSave()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        await using var db = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>(fixture.Options).AddInterceptors(new FailOneSave()).Options);
        var service = fixture.Service(db);
        Assert.Equal(503, (await Assert.ThrowsAsync<AgentOperationException>(() => service.IssueTokenAsync(fixture.OwnerId, 1))).StatusCode);
        Assert.Empty(db.ChangeTracker.Entries());
        await service.IssueTokenAsync(fixture.OwnerId, 1);
        await using var inspection = fixture.CreateContext();
        Assert.Single(await inspection.Set<EnrollmentTokenEntity>().ToListAsync());
    }

    [Fact]
    public async Task EncryptionFailureDoesNotConsumeCredentialOrExposeProtectionException()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        var protection = new FailingProtection(fixture.Protection) { FailProtect = true };
        await using var db = fixture.CreateContext();
        var service = fixture.Service(db, protection);
        var error = await Assert.ThrowsAsync<AgentOperationException>(() => service.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow())));
        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain("secret", error.ToString());
        protection.FailProtect = false;
        Assert.Equal(AgentEnrollmentProtocol.Pending, (await service.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()))).Status);
        Assert.Single(await db.Set<AgentEntity>().ToListAsync());
        Assert.Single(await db.Set<AgentNonceEntity>().ToListAsync());
    }

    [Fact]
    public async Task EnvironmentWriteFailureLeavesOriginalCiphertextAndVersionIntact()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await AgentEnvironmentTests.RegisterAgentAsync(fixture);
        await using var db = fixture.CreateContext();
        var originalCiphertext = (await db.Set<AgentEntity>().AsNoTracking().SingleAsync()).ProtectedEnvironment;
        var protection = new FailingProtection(fixture.Protection) { FailProtect = true };
        var service = fixture.Service(db, protection);
        var error = await Assert.ThrowsAsync<AgentOperationException>(() => service.SaveEnvironmentAsync(fixture.OwnerId, agent.Id, new("SECRET=uncommitted", 0)));
        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.OrdinalIgnoreCase);
        protection.FailProtect = false;
        Assert.Equal(new AgentEnvironment("", 0), await service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id));
        Assert.Equal(originalCiphertext, (await db.Set<AgentEntity>().AsNoTracking().SingleAsync()).ProtectedEnvironment);
        await service.SaveEnvironmentAsync(fixture.OwnerId, agent.Id, new("SECRET=committed", 0));
        Assert.Equal(new AgentEnvironment("SECRET=committed", 1), await service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id));
    }

    [Theory]
    [InlineData("not a protected payload !")]
    [InlineData("")]
    [InlineData("AAAA")]
    public async Task CorruptCiphertextReturnsSanitizedUnavailableWithoutMutatingStoredValue(string corrupted)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await AgentEnvironmentTests.RegisterAgentAsync(fixture);
        await using var db = fixture.CreateContext();
        await db.Set<AgentEntity>().ExecuteUpdateAsync(update => update.SetProperty(entity => entity.ProtectedEnvironment, corrupted));
        var error = await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id)));
        Assert.Equal(503, error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.Equal(corrupted, (await db.Set<AgentEntity>().AsNoTracking().SingleAsync()).ProtectedEnvironment);
    }

    [Fact]
    public async Task WrongProtectionKeysCannotReadEnvironment()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await AgentEnvironmentTests.RegisterAgentAsync(fixture);
        await using var db = fixture.CreateContext();
        var error = await Assert.ThrowsAsync<AgentOperationException>(() => fixture.Service(db, new EphemeralDataProtectionProvider())
            .ReadEnvironmentAsync(fixture.OwnerId, agent.Id));
        Assert.Equal(503, error.StatusCode);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public async Task FreshnessAndInitialExpiryAreRecheckedAfterGuardAcquisition(int advanceMinutes)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        var request = key.Request(issued.Token, fixture.Clock.GetUtcNow());
        var interceptor = new AdvanceAtGuard(fixture.Clock, TimeSpan.FromMinutes(advanceMinutes));
        await using var db = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>(fixture.Options).AddInterceptors(interceptor).Options);
        Assert.Equal(401, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.Service(db).RegisterAsync(request))).StatusCode);
        Assert.Empty(await db.Set<AgentEntity>().ToListAsync());
        Assert.Null((await db.Set<EnrollmentTokenEntity>().SingleAsync()).BoundIdentity);
    }

    [Fact]
    public async Task CallerCancellationDoesNotConsumeCredential()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        using var key = new EnrollmentKey();
        var issued = await fixture.IssueAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync(service =>
            service.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()), cancellation.Token)));
        Assert.Empty(await fixture.ListAsync());
        Assert.Equal(AgentEnrollmentProtocol.Pending, (await fixture.RegisterAsync(key.Request(issued.Token, fixture.Clock.GetUtcNow()))).Status);
    }

    private sealed class FailOneSave : SaveChangesInterceptor
    {
        private bool fail = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (fail) { fail = false; throw new DbUpdateException("secret database credential and SQL payload"); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AdvanceAtGuard(MutableEnrollmentClock clock, TimeSpan time) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"SecurityGuard\"", StringComparison.Ordinal)) clock.Advance(time);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailingProtection(IDataProtectionProvider inner) : IDataProtectionProvider
    {
        public bool FailProtect { get; set; }
        public IDataProtector CreateProtector(string purpose) => new Protector(inner.CreateProtector(purpose), this);
        private sealed class Protector(IDataProtector inner, FailingProtection owner) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new Protector(inner.CreateProtector(purpose), owner);
            public byte[] Protect(byte[] plaintext) => owner.FailProtect
                ? throw new CryptographicException("secret protection key diagnostic") : inner.Protect(plaintext);
            public byte[] Unprotect(byte[] protectedData) => inner.Unprotect(protectedData);
        }
    }
}
