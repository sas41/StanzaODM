using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Core.Security;

namespace Stanza.Tests.Operations;

internal sealed class OperationTestHarness : IAsyncDisposable
{
    private readonly string directory = Path.Combine("/tmp/opencode", "stanza-operation-tests-" + Guid.NewGuid().ToString("N"));
    private OperationWorker? worker;
    public ServiceProvider Services { get; private set; } = null!;
    public OperationTestClock Clock { get; } = new();
    public OperationTestDispatcher Dispatcher { get; } = new();
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public Guid Root { get; private set; }
    public Guid User { get; private set; }
    public Guid Agent { get; private set; }
    public Guid Deployment { get; private set; }
    public const string Password = "a-secure-password-123";
    public const string GitUrl = "https://git.example.test/team/app.git";

    public static async Task<OperationTestHarness> CreateAsync()
    {
        var harness = new OperationTestHarness();
        Directory.CreateDirectory(harness.directory);
        var registrations = new ServiceCollection();
        registrations.AddSingleton<TimeProvider>(harness.Clock);
        registrations.AddSingleton(harness.Protection);
        registrations.AddSingleton<IAgentCommandDispatcher>(harness.Dispatcher);
        registrations.AddDbContext<StanzaDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(harness.directory, "state.db")};Default Timeout=5;Pooling=False"));
        registrations.AddScoped<AccountService>(); registrations.AddScoped<AccessService>();
        registrations.AddScoped<DeploymentDraftService>(); registrations.AddScoped<InstanceSettingsService>();
        registrations.AddScoped<OperationalPolicyService>(); registrations.AddScoped<AgentPolicyService>();
        registrations.AddScoped<AgentAdministrationService>(); registrations.AddScoped<AuditService>();
        registrations.AddScoped<AutomationAuthorization>(); registrations.AddScoped<AutomationService>(); registrations.AddScoped<AutomationOperationGuard>();
        registrations.AddScoped<SourceService>();
        registrations.AddMemoryCache(); registrations.AddScoped<ActionInputService>(); registrations.AddScoped<DeploymentActionOverview>();
        registrations.AddScoped<ISourceSnapshotProvider>(services => services.GetRequiredService<SourceService>());
        registrations.AddScoped<OperationService>();
        harness.Services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await harness.InScope(async services =>
        {
            var db = services.GetRequiredService<StanzaDbContext>();
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            var accounts = services.GetRequiredService<AccountService>();
            harness.Root = (await accounts.BootstrapAsync("root", Password)).Id;
            harness.User = (await accounts.CreateAsync(harness.Root, "ordinary", Password, AccountRole.User)).Id;
            return true;
        });
        harness.Agent = await harness.AddAgentAsync("primary", "SHARED=agent\nAGENT_SECRET=agent-secret\n");
        harness.Deployment = await harness.AddDeploymentAsync("App");
        await harness.InScope(async services =>
        {
            await services.GetRequiredService<InstanceSettingsService>().SaveAsync(harness.Root,
                new("https://stanza.example.test", "SHARED=global\nGLOBAL_SECRET=global-secret\n", 0));
            return true;
        });
        return harness;
    }

    public async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
    public Task<Guid> AddAgentAsync(string name = "other", string environment = "SHARED=other\n") => InScope(async services =>
    {
        var id = Guid.NewGuid(); var db = services.GetRequiredService<StanzaDbContext>();
        db.Add(new AgentEntity { Id = id, Identity = id.ToString("N"), Hostname = name, Status = "approved", RegisteredAt = Clock.GetUtcNow().ToUnixTimeSeconds(),
            ProtectedEnvironment = Protection.CreateProtector("Stanza.AgentEnvironment.v1", id.ToString("N")).Protect(environment) });
        await db.SaveChangesAsync(); return id;
    });
    public Task<Guid> AddDeploymentAsync(string name = "Other", Guid? agent = null) => InScope(async services =>
        (await services.GetRequiredService<DeploymentDraftService>().CreateAsync(Root, name, agent ?? Agent,
            "# compose-secret\nservices: {app: {image: example/app:latest}}\n", "SHARED=deployment\nDEPLOYMENT_SECRET=deployment-secret\n")).Id);
    public Task<GrantSummary> GrantAsync(string action, string type, Guid? id) => InScope(services =>
        services.GetRequiredService<AccessService>().AddGrantAsync(Root, User, null, action, type, id));
    public Task RevokeAsync(Guid grant) => InScope(async services =>
    { await services.GetRequiredService<AccessService>().RemoveGrantAsync(Root, grant); return true; });
    public async Task AuthorizeUserAsync(Guid? deployment = null, Guid? agent = null, string action = "deployment.deploy")
    {
        await GrantAsync("deployment.read", "deployment", deployment ?? Deployment);
        if (action != "deployment.read") await GrantAsync(action, "deployment", deployment ?? Deployment);
        await GrantAsync("agent.read", "agent", agent ?? Agent);
    }
    public Task<CredentialSummary> GitCredentialAsync() => InScope(services => services.GetRequiredService<SourceService>().SaveCredentialAsync(Root, null, 0,
        new("Git", "git", GitUrl, "git-user-secret", "git-password-secret")));
    public Task<CredentialSummary> RegistryCredentialAsync() => InScope(services => services.GetRequiredService<SourceService>().SaveCredentialAsync(Root, null, 0,
        new("Registry", "registry", "registry.example.test", "registry-user-secret", "registry-password-secret")));
    public Task<GitSourceSummary> AddSourceAsync(Guid? credential = null) => InScope(services => services.GetRequiredService<SourceService>()
        .SaveGitAsync(Root, Deployment, null, 0, new(GitUrl, "main", "sources/app", credential)));
    public Task<OperationSummary> EnqueueAsync(string kind = "deploy", Guid? actor = null, Guid? deployment = null, Guid? target = null,
        AgentExecution? execution = null, string trigger = "manual") => InScope(services => services.GetRequiredService<OperationService>()
            .EnqueueAsync(new(actor ?? Root, deployment ?? Deployment, kind, trigger, target, execution)));
    public Task<OperationEntity> RowAsync(Guid id) => InScope(services => services.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().AsNoTracking().SingleAsync(x => x.Id == id));
    public AgentWorkRequest Decode(OperationEntity row) => JsonSerializer.Deserialize<AgentWorkRequest>(Protection.CreateProtector("Stanza.Operation.v1", row.Id.ToString("N")).Unprotect(row.ProtectedRequest))!;
    public Task<OperationDetail> DetailAsync(Guid id, Guid? actor = null) => InScope(services => services.GetRequiredService<OperationService>().DetailAsync(actor ?? Root, id));
    public Task CancelAsync(Guid id, Guid? actor = null) => InScope(async services =>
    { await services.GetRequiredService<OperationService>().CancelAsync(actor ?? Root, id); return true; });
    public Task ReconcileAsync(Guid id, Guid? actor = null) => InScope(async services =>
    { await services.GetRequiredService<OperationService>().ReconcileAsync(actor ?? Root, id); return true; });
    public Task SetStateAsync(Guid id, string state, long? completedAt = null, long leaseUntil = 0) => InScope(async services =>
    {
        await services.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, state).SetProperty(x => x.CompletedAt, completedAt).SetProperty(x => x.LeaseUntil, leaseUntil));
        return true;
    });
    public async Task StartWorkerAsync()
    {
        Assert.Null(worker);
        worker = new(Services.GetRequiredService<IServiceScopeFactory>(), Dispatcher, Clock, NullLogger<OperationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
    }
    public async Task StopWorkerAsync()
    {
        if (worker is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(timeout.Token);
        worker.Dispose(); worker = null;
    }
    public async Task<OperationEntity> WaitStateAsync(Guid id, params string[] states)
    {
        OperationEntity? row = null;
        await UntilAsync(async () => { row = await RowAsync(id); return states.Contains(row.State, StringComparer.Ordinal); },
            $"Operation {id} did not reach {string.Join('/', states)}; last state: ");
        return row!;
    }
    public static async Task UntilAsync(Func<Task<bool>> condition, string message = "Condition was not reached")
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail(message);
    }
    public async Task NextTickAsync()
    {
        // Let continuations finish installing their timers before advancing the controlled clock.
        await Task.Delay(20);
        Clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
    }
    public Task WaitForAuditAsync(Guid operation, string action) => UntilAsync(() => InScope(services =>
        services.GetRequiredService<StanzaDbContext>().Set<AuditEvent>().AnyAsync(x => x.Resource == operation.ToString() && x.Action == action)),
        "The worker did not persist its audit event.");
    public async ValueTask DisposeAsync()
    {
        await StopWorkerAsync();
        await Services.DisposeAsync();
        // Only fixture-created files in this unique directory are removed.
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
}

internal sealed class OperationTestDispatcher : IAgentCommandDispatcher
{
    private readonly ConcurrentQueue<(Guid Agent, AgentWorkRequest Request)> dispatched = new();
    private readonly SemaphoreSlim arrivals = new(0);
    public IReadOnlyList<(Guid Agent, AgentWorkRequest Request)> Requests => dispatched.ToArray();
    public bool Connected { get; set; } = true;
    public Func<Guid, AgentWorkRequest, CancellationToken, Task<AgentWorkResponse>> Handler { get; set; } =
        (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, true, "ok", "output-secret"));
    public bool IsConnected(Guid agentId) => Connected;
    public Task<AgentWorkResponse> DispatchAsync(Guid agentId, AgentWorkRequest request, CancellationToken token = default)
    {
        dispatched.Enqueue((agentId, request)); arrivals.Release();
        return Handler(agentId, request, token);
    }
    public async Task WaitForCountAsync(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dispatched.Count < count) await arrivals.WaitAsync(timeout.Token);
    }
}

internal sealed class OperationTestClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<Timer> timers = [];
    private DateTimeOffset now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
    public override long GetTimestamp() => GetUtcNow().UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public int PendingTimerCount { get { lock (gate) return timers.Count(x => !x.Disposed && x.Due.HasValue); } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            var timer = new Timer(this, callback, state); timers.Add(timer);
            timer.Change(dueTime, period); return timer;
        }
    }
    public void Advance(TimeSpan amount)
    {
        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (gate)
        {
            now += amount;
            foreach (var timer in timers.ToArray())
                if (!timer.Disposed && timer.Due is { } due && due <= now)
                {
                    callbacks.Add((timer.Callback, timer.State));
                    timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? null : now + timer.Period;
                }
        }
        foreach (var (callback, state) in callbacks) callback(state);
    }
    private sealed class Timer(OperationTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset? Due { get; set; }
        public TimeSpan Period { get; set; }
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime; Period = period; return true;
            }
        }
        public void Dispose() { lock (clock.gate) { Disposed = true; clock.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
