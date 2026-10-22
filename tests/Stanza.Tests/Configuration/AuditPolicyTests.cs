using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Security;

namespace Stanza.Tests.Configuration;

public sealed class AuditPolicyTests
{
    [Fact]
    public async Task AuditMetadataIsBoundedControlCharacterFreeAndUsesTheControlledClock()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var db = fixture.Context(); var audit = new AuditService(db, fixture.Clock, fixture.Access(db));
        await audit.WriteAsync(fixture.Owner, "\n" + new string('a', 250), "\0" + new string('r', 250), "\r" + new string('s', 150));
        var row = Assert.Single(await audit.ListAsync(fixture.Owner, null));
        Assert.Equal(new string('a', 200), row.Action); Assert.Equal(new string('r', 200), row.Resource); Assert.Equal(new string('s', 100), row.Result);
        Assert.Equal(fixture.Clock.GetUtcNow().ToUnixTimeSeconds(), row.At); Assert.Equal(fixture.Owner, row.ActorId);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), x => x.Entity is AuditEvent);
    }

    [Fact]
    public async Task AuditReadsAreDefaultDenyRequireExactGrantAndRecheckAccountDisable()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var actor = await fixture.AccountAsync(); await using var db = fixture.Context(); var audit = new AuditService(db, fixture.Clock, fixture.Access(db));
        await audit.WriteAsync(fixture.Owner, "settings.changed", "instance", "saved");
        await Assert.ThrowsAsync<SecurityOperationException>(() => audit.ListAsync(actor, null));
        await fixture.GrantAsync(actor, "settings.read"); await Assert.ThrowsAsync<SecurityOperationException>(() => audit.ListAsync(actor, null));
        await fixture.GrantAsync(actor, "audit.read"); Assert.Single(await audit.ListAsync(actor, "settings")); Assert.Empty(await audit.ListAsync(actor, "missing"));
        await new AccountService(db, fixture.Clock).SetEnabledAsync(fixture.Owner, actor, false);
        await Assert.ThrowsAsync<SecurityOperationException>(() => audit.ListAsync(actor, null));
    }

    [Fact]
    public async Task AuditHistoryRetrievalIsBoundedToFiveHundredNewestEntries()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var db = fixture.Context();
        db.Set<AuditEvent>().AddRange(Enumerable.Range(0, 600).Select(i => new AuditEvent { Id = Guid.NewGuid(), ActorId = fixture.Owner, Action = "policy.changed", Resource = "instance", Result = "saved", At = i }));
        await db.SaveChangesAsync(); var rows = await new AuditService(db, fixture.Clock, fixture.Access(db)).ListAsync(fixture.Owner, null);
        Assert.Equal(500, rows.Count); Assert.Equal(599, rows[0].At); Assert.Equal(100, rows[^1].At);
    }
}
