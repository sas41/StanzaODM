using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;

namespace Stanza.Tests.Configuration;

internal sealed class ConfigurationTestStore : IAsyncDisposable
{
    public const string Password = "a-secure-password-123";
    private readonly string root = Path.Combine("/tmp/opencode", "stanza-configuration-" + Guid.NewGuid().ToString("N"));
    public Guid Owner { get; private set; }
    public ManualPolicyClock Clock { get; } = new();
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public StanzaDbContext Context() => new(new DbContextOptionsBuilder<StanzaDbContext>()
        .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False;Default Timeout=10").Options);
    public AccessService Access(StanzaDbContext db) => new(db, Clock);
    public OperationalPolicyService Policies(StanzaDbContext db) => new(db, Access(db));
    public InstanceSettingsService Settings(StanzaDbContext db) => new(db, Protection);
    public AgentPolicyService AgentPolicies(StanzaDbContext db) => new(db, Access(db), Policies(db));
    public AgentAdministrationService Agents(StanzaDbContext db) => new(db, Protection, Clock);
    public static async Task<ConfigurationTestStore> CreateAsync()
    {
        var fixture = new ConfigurationTestStore(); Directory.CreateDirectory(fixture.root);
        await using var db = fixture.Context(); await db.Database.EnsureCreatedAsync();
        fixture.Owner = (await new AccountService(db, fixture.Clock).BootstrapAsync("owner", Password)).Id;
        return fixture;
    }
    public async Task<Guid> AccountAsync(AccountRole role = AccountRole.User)
    {
        await using var db = Context();
        return (await new AccountService(db, Clock).CreateAsync(Owner, "actor" + Guid.NewGuid().ToString("N"), Password, role)).Id;
    }
    public async Task GrantAsync(Guid actor, string action, string resource = "instance", Guid? id = null)
    {
        await using var db = Context(); await Access(db).AddGrantAsync(Owner, actor, null, action, resource, id);
    }
    public async Task<Guid> AgentAsync()
    {
        await using var db = Context(); var id = Guid.NewGuid();
        db.Set<AgentEntity>().Add(new() { Id = id, Identity = Guid.NewGuid().ToString("N"), Hostname = "policy-agent", PublicKey = "public-key", Status = "approved" });
        await db.SaveChangesAsync(); return id;
    }
    public ValueTask DisposeAsync() { Directory.Delete(root, true); return ValueTask.CompletedTask; }
}

internal sealed class ManualPolicyClock : TimeProvider
{
    private long ticks = TimeProvider.System.GetUtcNow().Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
}
