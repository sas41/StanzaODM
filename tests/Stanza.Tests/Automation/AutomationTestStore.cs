using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;

namespace Stanza.Tests.Automation;

public sealed class AutomationClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}

public sealed class AutomationTestStore : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    public StanzaDbContext Db { get; private set; } = null!;
    public AutomationClock Clock { get; } = new();
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public AccountService Accounts { get; private set; } = null!;
    public AccessService Access { get; private set; } = null!;
    public AutomationAuthorization Authorization { get; private set; } = null!;
    public AutomationService Service { get; private set; } = null!;
    public Guid Root { get; private set; }
    public Guid User { get; private set; }
    public Guid Deployment { get; } = Guid.NewGuid();
    public Guid OtherDeployment { get; } = Guid.NewGuid();
    public FakeOperations Operations { get; } = new();

    public static async Task<AutomationTestStore> Create()
    {
        var store = new AutomationTestStore(); await store.connection.OpenAsync();
        store.Db = store.NewContext(); await store.Db.Database.EnsureCreatedAsync();
        store.Accounts = new(store.Db, store.Clock); store.Access = new(store.Db, store.Clock);
        store.Authorization = new(store.Access); store.Service = new(store.Db, store.Authorization, store.Protection, store.Clock);
        store.Root = (await store.Accounts.BootstrapAsync("root", "a-secure-password-123")).Id;
        store.User = (await store.Accounts.CreateAsync(store.Root, "user", "a-secure-password-123", AccountRole.User)).Id;
        return store;
    }
    public StanzaDbContext NewContext() => new(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(connection).Options);
    public AutomationExecutor Executor(IImageDigestProbe? probe = null) => new(Db, Service, Authorization, Operations, probe ?? new UnavailableImageDigestProbe(), Clock);
    public AutomationScheduler Scheduler() => new(Db, Service, Authorization, Clock);
    public WebhookReceiver Receiver() => new(Db, Service, Authorization);
    public Task<GrantSummary> Grant(string permission, string type, Guid? id) => Access.AddGrantAsync(Root, User, null, permission, type, id);
    public Task<ActionDefinition> Action(ActionPlan? plan = null, string concurrency = "queue") => Service.SaveActionAsync(Root, null, 0, "Action", true, concurrency,
        plan ?? new ActionPlan([new("deploy", Deployment)]));
    public Task<AutomationSchedule> Schedule(Guid? action = null, string missed = "skip", string overlap = "skip") => Service.SaveScheduleAsync(Root,
        new() { Name = "Schedule", DeploymentId = Deployment, ActionId = action, Expression = "* * * * *", Enabled = true, MissedRun = missed, Overlap = overlap }, 0);
    public Task<WebhookSaved> Hook(Guid? action = null, string mapping = "{}", string refs = "") => Service.SaveWebhookAsync(Root,
        new() { Name = "Webhook", DeploymentId = Deployment, ActionId = action, ParameterMapping = mapping, Refs = refs, Enabled = true }, 0);

    public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
}

public sealed class FakeOperations : IOperationService
{
    public List<OperationSubmission> Submissions { get; } = [];
    public List<Guid> Cancelled { get; } = [];
    private readonly Dictionary<Guid, OperationSummary> results = [];
    public string State { get; set; } = "Succeeded";
    public bool ThrowAfterEnqueue { get; set; }
    public Func<CancellationToken, Task>? OnWait { get; set; }
    public Task<OperationSummary> EnqueueAsync(OperationSubmission submission, CancellationToken token = default)
    {
        Submissions.Add(submission);
        if (ThrowAfterEnqueue) throw new IOException("secret process error must not be logged");
        var result = new OperationSummary(Guid.NewGuid(), submission.ActorId, submission.DeploymentId, submission.AgentId ?? Guid.NewGuid(),
            submission.Kind, State, submission.Trigger, 0, 0, null);
        results.Add(result.Id, result); return Task.FromResult(result);
    }
    public async Task<OperationSummary> WaitAsync(Guid actorId, Guid operationId, CancellationToken token = default)
    {
        if (OnWait is not null) await OnWait(token);
        return results[operationId];
    }
    public Task CancelAsync(Guid actorId, Guid operationId, CancellationToken token = default) { Cancelled.Add(operationId); return Task.CompletedTask; }
}
