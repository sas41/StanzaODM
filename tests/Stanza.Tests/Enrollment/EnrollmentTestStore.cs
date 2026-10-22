using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Enrollment;

internal sealed class EnrollmentTestStore : IAsyncDisposable
{
    private readonly string root = Path.Combine("/tmp/opencode", "stanza-enrollment-test-" + Guid.NewGuid().ToString("N"));
    public Guid OwnerId { get; private set; }
    public MutableEnrollmentClock Clock { get; } = new();
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public DbContextOptions<StanzaDbContext> Options => new DbContextOptionsBuilder<StanzaDbContext>()
        .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Default Timeout=15;Pooling=False").Options;

    public static async Task<EnrollmentTestStore> CreateAsync(bool bootstrap = true)
    {
        var fixture = new EnrollmentTestStore();
        Directory.CreateDirectory(fixture.root);
        await using var db = fixture.CreateContext();
        await db.Database.EnsureCreatedAsync();
        if (bootstrap)
            fixture.OwnerId = (await new AccountService(db, fixture.Clock).BootstrapAsync("owner", "a-secure-test-password-123")).Id;
        return fixture;
    }

    public StanzaDbContext CreateContext() => new(Options);
    public AgentAdministrationService Service(StanzaDbContext db, IDataProtectionProvider? protection = null) => new(db, protection ?? Protection, Clock);

    public async Task<T> RunAsync<T>(Func<AgentAdministrationService, Task<T>> operation)
    {
        await using var db = CreateContext();
        return await operation(Service(db));
    }

    public async Task RunAsync(Func<AgentAdministrationService, Task> operation)
    {
        await using var db = CreateContext();
        await operation(Service(db));
    }

    public Task<IssuedEnrollmentToken> IssueAsync(int minutes = 1) => RunAsync(service => service.IssueTokenAsync(OwnerId, minutes));
    public Task<AgentEnrollmentResponse> RegisterAsync(AgentEnrollmentRequest? request) => RunAsync(service => service.RegisterAsync(request));
    public Task<IReadOnlyList<AgentSummary>> ListAsync() => RunAsync(service => service.ListAsync(OwnerId));
    public Task SetStatusAsync(Guid id, string status) => RunAsync(service => service.SetStatusAsync(OwnerId, id, status));

    public ValueTask DisposeAsync()
    {
        Directory.Delete(root, true);
        return ValueTask.CompletedTask;
    }
}

internal sealed class MutableEnrollmentClock : TimeProvider
{
    private long seconds = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref seconds));
    public void Advance(TimeSpan time) => Interlocked.Add(ref seconds, (long)time.TotalSeconds);
}

internal sealed class EnrollmentKey : IDisposable
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string PublicKey => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    public string Identity => AgentEnrollmentProtocol.GetAgentId(key.ExportSubjectPublicKeyInfo());

    public AgentEnrollmentRequest Request(string token, DateTimeOffset now, string hostname = "test-host", string? nonce = null) =>
        Sign(new(token, AgentEnrollmentProtocol.Name, AgentEnrollmentProtocol.Version, Identity, PublicKey, hostname,
            now.ToUnixTimeSeconds(), nonce ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), ""));

    public AgentEnrollmentRequest Sign(AgentEnrollmentRequest request) => request with
    {
        Signature = Convert.ToBase64String(key.SignData(AgentEnrollmentProtocol.SigningPayload(request),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    };

    public void Dispose() => key.Dispose();
}
