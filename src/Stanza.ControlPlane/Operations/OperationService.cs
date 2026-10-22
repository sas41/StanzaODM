using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Automation;

namespace Stanza.ControlPlane.Operations;

public sealed class OperationException(string message) : Exception(message);
public sealed record OperationDetail(OperationSummary Summary, string? Output)
{
    public override string ToString() => $"OperationDetail {{ Id = {Summary.Id}, Output = [REDACTED] }}";
}

public sealed class OperationService(StanzaDbContext db, DeploymentDraftService drafts, AccessService access,
    InstanceSettingsService settings, OperationalPolicyService policies, ISourceSnapshotProvider sources,
    IDataProtectionProvider protection, TimeProvider clock, AuditService audit, AgentPolicyService agentPolicies, InstanceMaintenance? maintenance = null, AutomationOperationGuard? automationGuard = null) : IOperationService
{
    private static readonly IReadOnlyDictionary<string, string> Permissions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["validate"] = "deployment.validate", ["build"] = "deployment.build", ["pull"] = "deployment.deploy",
        ["deploy"] = "deployment.deploy", ["start"] = "deployment.start", ["stop"] = "deployment.stop",
        ["restart"] = "deployment.restart", ["remove"] = "deployment.remove", ["status"] = "deployment.read",
        ["logs"] = "logs.read", ["container-exec"] = "container.execute", ["image-digests"] = "automation.read"
    };
    public static bool Terminal(string state) => state is "Succeeded" or "Failed" or "Cancelled" or "Unknown";
    public async Task<OperationSummary> EnqueueAsync(OperationSubmission submission, CancellationToken token = default)
    {
        if (maintenance?.Paused == true) throw new OperationException("Recovery requires a Control Plane restart before execution can resume.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await SecurityWriteGuard.LockAsync(db, token);
        if (submission.Kind == "host-exec" && !(await policies.GetAsync(token)).Policy.AllowHostActions)
            throw new OperationException("Host actions are disabled in operational policy. Host namespace prerequisites are also required on the Agent.");
        var id = Guid.NewGuid();
        var files = new List<AgentFile>();
        var environments = new List<AgentEnvironmentFile>();
        var sourceSnapshot = new DeploymentSourcesSnapshot([], []);
        long revision = 0;
        Guid? target = submission.AgentId;
        if (submission.DeploymentId is { } deploymentId)
        {
            await AuthorizeAsync(submission.ActorId, deploymentId, submission.Kind, token);
            var snapshot = await drafts.SnapshotForExecutionAsync(submission.ActorId, deploymentId, ct: token);
            revision = snapshot.Revision;
            target ??= snapshot.AgentId;
            files.AddRange(snapshot.Files);
            var inherited = await settings.ReadInternalAsync(token);
            var agentEnvironment = "";
            if (target is { } environmentAgent)
            {
                var agent = await db.Set<AgentEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == environmentAgent, token)
                    ?? throw new OperationException("The target Agent does not exist.");
                agentEnvironment = protection.CreateProtector("Stanza.AgentEnvironment.v1").CreateProtector(environmentAgent.ToString("N")).Unprotect(agent.ProtectedEnvironment);
            }
            environments.Add(new("global.env", inherited.GlobalEnvironment));
            environments.Add(new("agent.env", agentEnvironment));
            environments.Add(new("deployment.env", snapshot.EnvironmentText));
            sourceSnapshot = await sources.PrepareAsync(submission.ActorId, deploymentId, token);
        }
        else if (submission.Kind != "host-exec") throw new OperationException("A deployment is required for this operation.");
        if (target is null || target == Guid.Empty) throw new OperationException("Assign an approved Agent before running this operation.");
        if (submission.Kind == "host-exec") await RequireAsync(submission.ActorId, "host.execute", "agent", target, token);
        else await RequireAsync(submission.ActorId, "agent.read", "agent", target, token);
        if (!await db.Set<AgentEntity>().AnyAsync(x => x.Id == target && x.Status == "approved", token))
            throw new OperationException("The target Agent is not approved.");
        if (submission.AgentId.HasValue && submission.DeploymentId.HasValue)
            await RequireAsync(submission.ActorId, "deployment.move", "deployment", submission.DeploymentId, token);
        if (submission.Kind is "host-exec" or "container-exec" && submission.Execution is null)
            throw new OperationException("An executable and argument list are required.");
        if (submission.Execution is { } execution && (string.IsNullOrWhiteSpace(execution.Executable) || execution.Executable.Contains('\0') || execution.Arguments.Count > 256 || execution.Arguments.Any(x => x is null || x.Length > 8192 || x.Contains('\0'))))
            throw new OperationException("Invalid command arguments.");
        if (submission.Trigger.Length is < 1 or > 100 || submission.Trigger.Any(char.IsControl)) throw new OperationException("Invalid operation trigger.");
        var request = new AgentWorkRequest(id, submission.DeploymentId ?? Guid.Empty, submission.Kind, files, environments,
            submission.Execution, sourceSnapshot.Git, sourceSnapshot.Registries, await agentPolicies.RuntimeAsync(target.Value, token));
        var serialized = JsonSerializer.Serialize(request);
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > 30 * 1024 * 1024) throw new OperationException("The execution snapshot exceeds the transport limit.");
        var operation = new OperationEntity { Id = id, ActorId = submission.ActorId, DeploymentId = submission.DeploymentId,
            RequiresMovePermission = submission.AgentId.HasValue && submission.DeploymentId.HasValue,
            AgentId = target.Value, Revision = revision, Kind = submission.Kind, Trigger = submission.Trigger,
            CreatedAt = clock.GetUtcNow().ToUnixTimeSeconds(), ProtectedRequest = Protector(id).Protect(serialized) };
        db.Set<OperationEntity>().Add(operation);
        try { await db.SaveChangesAsync(token); await transaction.CommitAsync(token); }
        finally { db.Entry(operation).State = EntityState.Detached; }
        await audit.WriteAsync(submission.ActorId, "operation.queued", id.ToString(), submission.Kind, token);
        return Summary(operation);
    }

    public async Task<OperationSummary> WaitAsync(Guid actorId, Guid operationId, CancellationToken token = default)
    {
        while (true)
        {
            var detail = await DetailAsync(actorId, operationId, token);
            if (Terminal(detail.Summary.State)) return detail.Summary;
            await Task.Delay(TimeSpan.FromMilliseconds(250), clock, token);
        }
    }
    public async Task<IReadOnlyList<OperationSummary>> ListAsync(Guid actor, Guid? deploymentId = null, CancellationToken ct = default)
    {
        var query = db.Set<OperationEntity>().AsNoTracking();
        if (deploymentId.HasValue) query = query.Where(x => x.DeploymentId == deploymentId);
        var rows = await query.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(ct);
        var result = new List<OperationSummary>();
        foreach (var row in rows) if (await CanReadAsync(actor, row, ct)) result.Add(Summary(row));
        return result;
    }
    public async Task<OperationDetail> DetailAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        var row = await RequiredAsync(id, ct);
        if (!await CanReadAsync(actor, row, ct)) throw new OperationException("Operation read access is denied.");
        var output = row.ProtectedResponse is not null && (row.DeploymentId is { } deployment
            ? await access.IsAllowedAsync(actor, "logs.read", "deployment", deployment, ct)
            : await access.IsAllowedAsync(actor, "host.execute", "agent", row.AgentId, ct))
            ? JsonSerializer.Deserialize<AgentWorkResponse>(Protector(id).Unprotect(row.ProtectedResponse))?.Output : null;
        return new(Summary(row), output);
    }
    public async Task CancelAsync(Guid actorId, Guid operationId, CancellationToken token = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await SecurityWriteGuard.LockAsync(db, token);
        var row = await RequiredAsync(operationId, token);
        if (row.DeploymentId is { } deployment) await RequireAsync(actorId, "operations.cancel", "deployment", deployment, token);
        else await RequireAsync(actorId, "host.execute", "agent", row.AgentId, token);
        if (Terminal(row.State)) throw new OperationException("The operation has already finished.");
        var queued = row.State == "Queued";
        await db.Set<OperationEntity>().Where(x => x.Id == row.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, queued ? "Cancelled" : "CancellationRequested")
            .SetProperty(x => x.CompletedAt, queued ? clock.GetUtcNow().ToUnixTimeSeconds() : (long?)null), token);
        await transaction.CommitAsync(token);
        await audit.WriteAsync(actorId, "operation.cancel", operationId.ToString(), queued ? "cancelled_before_dispatch" : "requested", token);
    }
    public async Task ReconcileAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        var row = await RequiredAsync(id, ct);
        if (row.DeploymentId is not { } deployment || row.State != "Unknown") throw new OperationException("Only uncertain deployment operations can be reconciled.");
        await RequireAsync(actor, "operations.cancel", "deployment", deployment, ct);
        var uncertaintyAt = row.CompletedAt ?? clock.GetUtcNow().ToUnixTimeSeconds();
        if (!await db.Set<OperationEntity>().AnyAsync(x => x.DeploymentId == deployment && x.AgentId == row.AgentId && x.Kind == "status" && x.State == "Succeeded" && x.CreatedAt >= uncertaintyAt && x.CompletedAt >= uncertaintyAt, ct))
            throw new OperationException("Run a fresh successful status inspection on the same Agent before acknowledging an uncertain outcome.");
        await db.Set<OperationEntity>().Where(x => x.Id == id && x.State == "Unknown").ExecuteUpdateAsync(update => update.SetProperty(x => x.State, "Failed").SetProperty(x => x.Code, "reconciled_not_retried"), ct);
        await transaction.CommitAsync(ct);
        await audit.WriteAsync(actor, "operation.reconciled", id.ToString(), "not_retried", ct);
    }
    internal async Task AuthorizeExecutionAsync(OperationEntity row, CancellationToken ct)
    {
        if (maintenance?.Paused == true) throw new OperationException("Execution is paused for instance recovery.");
        if (row.DeploymentId is { } deployment)
        {
            await AuthorizeAsync(row.ActorId, deployment, row.Kind, ct);
            if (row.RequiresMovePermission) await RequireAsync(row.ActorId, "deployment.move", "deployment", deployment, ct);
            var current = await sources.PrepareAsync(row.ActorId, deployment, ct);
            var captured = Request(row);
            if (JsonSerializer.Serialize(current.Git) != JsonSerializer.Serialize(captured.Sources ?? []) || JsonSerializer.Serialize(current.Registries) != JsonSerializer.Serialize(captured.Registries ?? []))
                throw new OperationException("Source credentials or definitions changed; create a new operation snapshot.");
            await RequireAsync(row.ActorId, "agent.read", "agent", row.AgentId, ct);
        }
        else await RequireAsync(row.ActorId, "host.execute", "agent", row.AgentId, ct);
        if (!(await agentPolicies.RuntimeAsync(row.AgentId, ct)).ExecutionEnabled) throw new OperationException("Agent execution has been disabled.");
        if (row.Kind == "host-exec" && !(await policies.GetAsync(ct)).Policy.AllowHostActions) throw new OperationException("Host actions have been disabled.");
        if (!await db.Set<AgentEntity>().AnyAsync(x => x.Id == row.AgentId && x.Status == "approved", ct)) throw new OperationException("Agent approval has been revoked.");
        if (automationGuard is not null) await automationGuard.AuthorizeAsync(row.ActorId, row.Trigger, row.Kind, row.DeploymentId, row.AgentId, ct);
    }
    internal AgentWorkRequest Request(OperationEntity row) => JsonSerializer.Deserialize<AgentWorkRequest>(Protector(row.Id).Unprotect(row.ProtectedRequest)) ?? throw new OperationException("Invalid operation snapshot.");
    internal string ProtectResponse(Guid id, AgentWorkResponse response) => Protector(id).Protect(JsonSerializer.Serialize(response));
    internal static OperationSummary Summary(OperationEntity row) => new(row.Id, row.ActorId, row.DeploymentId, row.AgentId, row.Kind, row.State, row.Trigger, row.CreatedAt, row.CompletedAt, row.Code);
    private IDataProtector Protector(Guid id) => protection.CreateProtector("Stanza.Operation.v1", id.ToString("N"));
    private Task<OperationEntity> RequiredAsync(Guid id, CancellationToken ct) => RequiredCoreAsync(id, ct);
    private async Task<OperationEntity> RequiredCoreAsync(Guid id, CancellationToken ct) => await db.Set<OperationEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new OperationException("Operation not found.");
    private async Task<bool> CanReadAsync(Guid actor, OperationEntity row, CancellationToken ct) => row.DeploymentId is { } deployment
        ? await access.IsAllowedAsync(actor, "deployment.read", "deployment", deployment, ct)
        : await access.IsAllowedAsync(actor, "host.execute", "agent", row.AgentId, ct);
    private async Task AuthorizeAsync(Guid actor, Guid deployment, string kind, CancellationToken ct)
    {
        if (kind == "host-exec") throw new OperationException("Host actions must explicitly target an Agent without borrowing a deployment identity.");
        if (!Permissions.TryGetValue(kind, out var permission)) throw new OperationException("Unsupported operation.");
        await RequireAsync(actor, permission, "deployment", deployment, ct);
    }
    private async Task RequireAsync(Guid actor, string action, string type, Guid? id, CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(actor, action, type, id, ct)) throw new OperationException($"Permission required: {action}.");
    }
}
