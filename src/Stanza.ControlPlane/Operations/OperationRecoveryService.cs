using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Operations;

/// <summary>Inspect without replaying commands, then resolve operations and their run in one transaction.</summary>
public sealed class OperationRecoveryService(StanzaDbContext db, OperationService operations, AccessService access,
    AutomationService automation, AutomationAuthorization authorization, IAgentCommandDispatcher dispatcher,
    OperationOutputStore output, AgentPolicyService policies, TimeProvider clock)
{
    public async Task<string> InspectOperationAsync(Guid actor, Guid id, bool acknowledgeApplied, CancellationToken ct = default)
    {
        var row = await db.Set<OperationEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new OperationException("Operation not found.");
        var run = await CorrelatedRunAsync(row, ct);
        return await RecoverAsync(actor, [row], run, acknowledgeApplied, ct);
    }
    public async Task<string> InspectRunAsync(Guid actor, Guid id, bool acknowledgeApplied, CancellationToken ct = default)
    {
        var run = (await automation.ReadRunAsync(actor, id, ct)).Run;
        var rows = await RunOperationsAsync(run, ct);
        if (rows.Count == 0) throw new OperationException("No durable operation correlation is available. The lock remains held; do not replay this run.");
        return await RecoverAsync(actor, rows, run, acknowledgeApplied, ct);
    }
    internal async Task RecoverKnownAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Set<OperationEntity>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
        if (dispatcher is not IAgentJobDispatcher jobs) return;
        var run = await CorrelatedRunAsync(row, ct);
        if (row.State != "Unknown" && run?.State != "uncertain") return;
        var rows = run is null ? new List<OperationEntity> { row } : await RunOperationsAsync(run, ct);
        foreach (var item in rows.Where(x => x.State == "Unknown"))
        {
            var job = await jobs.InspectAsync(item.AgentId, item.Id, ct);
            // Automatic recovery uses established terminal outcomes only. Ambiguous apply always
            // needs explicit inspection/acknowledgement; no lease or elapsed timeout can substitute.
            if (!job.Found || job.Active || !job.Stopped || job.Response is null || job.Response.Code == "uncertain" || !job.Response.Succeeded && job.ApplyStarted) return;
        }
        await RecoverAsync(row.ActorId, rows, run, false, ct, automatic: true);
    }
    private Task<AutomationRun?> CorrelatedRunAsync(OperationEntity row, CancellationToken ct)
    {
        var trigger = row.Trigger;
        return trigger.StartsWith("automation:", StringComparison.Ordinal) && Guid.TryParseExact(trigger[11..], "N", out var runId)
            ? db.Set<AutomationRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, ct)
            : Task.FromResult<AutomationRun?>(null);
    }
    private async Task<List<OperationEntity>> RunOperationsAsync(AutomationRun run, CancellationToken ct)
    {
        var trigger = "automation:" + run.Id.ToString("N");
        var ids = await db.Set<AutomationStepRun>().Where(x => x.RunId == run.Id && x.OperationId != null).Select(x => x.OperationId).ToArrayAsync(ct);
        return await db.Set<OperationEntity>().AsNoTracking().Where(x => x.Trigger == trigger || x.Id == run.OperationId || ids.Contains(x.Id)).ToListAsync(ct);
    }
    private async Task RequireAsync(Guid actor, OperationEntity row, CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(actor, "agent.read", "agent", row.AgentId, ct) ||
            (row.DeploymentId is { } deployment
                ? !await access.IsAllowedAsync(actor, "operations.cancel", "deployment", deployment, ct) ||
                    !await access.IsAllowedAsync(actor, "deployment.read", "deployment", deployment, ct)
                : !await access.IsAllowedAsync(actor, "host.execute", "agent", row.AgentId, ct)))
            throw new OperationException("Recovery requires read and cancellation access to every affected target.");
    }
    private async Task AuthorizeAsync(Guid actor, IReadOnlyList<OperationEntity> rows, AutomationRun? run, CancellationToken ct)
    {
        foreach (var row in rows) await RequireAsync(actor, row, ct);
        if (run is null) return;
        try
        {
            await automation.ReadRunAsync(actor, run.Id, ct);
            if (run.ActionId is { } action) await authorization.RequireAsync(actor, "action.manage", "action", action, ct);
            if (run.Operation != "image-check") await authorization.PlanAsync(actor, automation.Unprotect<ActionPlan>(run.ProtectedPlan), ct);
        }
        catch (AutomationAccessException) { throw new OperationException("Automation recovery access is denied."); }
    }
    private async Task<string> RecoverAsync(Guid actor, List<OperationEntity> rows, AutomationRun? run, bool acknowledgeApplied, CancellationToken ct, bool automatic = false)
    {
        if (run is not null) rows = await RunOperationsAsync(run, ct);
        if (!automatic) await AuthorizeAsync(actor, rows, run, ct);
        if (dispatcher is not IAgentJobDispatcher jobs) throw new OperationException("The Agent does not support execution inspection.");
        var responses = new Dictionary<Guid, (AgentJobInspection Job, AgentWorkResponse Response)>();
        var statuses = new Dictionary<(Guid Deployment, Guid Agent), AgentWorkResponse>();
        foreach (var row in rows)
        {
            if (row.State is "Queued" or "Running" or "CancellationRequested")
                throw new OperationException("Work is still queued or owned by a live worker. Request cancellation or wait for its result; locks remain held.");
            if (row.State != "Unknown") continue;
            var job = await jobs.InspectAsync(row.AgentId, row.Id, ct);
            if (!job.Found || !job.Stopped || job.Active)
                throw new OperationException(job.Active ? $"The Agent is still executing {row.Id} ({job.Phase}). No locks were released."
                    : "The Agent cannot prove execution stopped. Missing or unfinished ledger evidence is not permission to release locks.");
            if (job.Response is null || !job.Response.Succeeded && job.ApplyStarted && !acknowledgeApplied ||
                job.Response.Code == "uncertain" && !job.ApplyStarted)
                throw new OperationException("The outcome is unresolved. Inspect the target and explicitly acknowledge applied side effects before reconciling; locks remain held.");
            async Task Append(Guid id, AgentOutputChunk chunk) { if (id == row.Id) await output.AppendAsync(id, chunk, ct); }
            var feed = dispatcher as IAgentWorkOutputSource;
            if (feed is not null) feed.Output += Append;
            AgentWorkResponse response;
            try { response = await jobs.AttachAsync(row.AgentId, row.Id, ct); }
            finally { if (feed is not null) feed.Output -= Append; }
            if (response.Code == "agent_busy" || response.Code != job.Response.Code || response.Succeeded != job.Response.Succeeded)
                throw new OperationException("Agent evidence changed during inspection. Retry inspection; locks remain held.");
            responses.Add(row.Id, (job, response));
            if (row.DeploymentId is { } deployment)
            {
                var request = new AgentWorkRequest(Guid.NewGuid(), deployment, "status", [], [], Policy: await policies.RuntimeAsync(row.AgentId, ct));
                var status = await dispatcher.DispatchAsync(row.AgentId, request, ct);
                if (!status.Succeeded || OperationWorker.Observe(status.Output) == "unknown")
                    throw new OperationException("A fresh container-status inspection failed. No locks were released.");
                statuses[(deployment, row.AgentId)] = status;
            }
        }
        if (run is not null && await db.Set<AutomationStepRun>().AnyAsync(x => x.RunId == run.Id &&
            (x.State == "running" || x.State == "uncertain") && x.OperationId == null, ct))
            throw new OperationException("A step has no durable operation correlation. Its outcome remains unresolved; no locks were released.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        if (!automatic) await AuthorizeAsync(actor, rows, run, ct);
        if (run is not null)
        {
            var currentIds = (await RunOperationsAsync(run, ct)).Select(x => x.Id).Order().ToArray();
            if (!currentIds.SequenceEqual(rows.Select(x => x.Id).Order())) throw new OperationException("Run operations changed. Inspect again.");
        }
        foreach (var row in rows)
        {
            var current = await db.Set<OperationEntity>().AsNoTracking().SingleAsync(x => x.Id == row.Id, ct);
            if (current.State != row.State) throw new OperationException("Operation state changed. Inspect again.");
            if (!responses.TryGetValue(row.Id, out var evidence)) continue;
            var state = evidence.Response.Succeeded ? "Succeeded" : "Failed";
            var code = evidence.Response.Code == "uncertain" ? "reconciled_not_retried" : evidence.Response.Code;
            await db.Set<OperationEntity>().Where(x => x.Id == row.Id && x.State == "Unknown")
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, state).SetProperty(x => x.Code, code)
                    .SetProperty(x => x.Stage, evidence.Job.Phase).SetProperty(x => x.CancellationReason, evidence.Job.CancellationReason)
                    .SetProperty(x => x.ProtectedResponse, operations.ProtectResponse(row.Id, evidence.Response))
                    .SetProperty(x => x.ProtectedAppliedMetadata, operations.ProtectResponse(row.Id, evidence.Response with { Output = null }))
                    .SetProperty(x => x.CompletedAt, clock.GetUtcNow().ToUnixTimeSeconds()), ct);
            await db.Set<AutomationStepRun>().Where(x => x.OperationId == row.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, state.ToLowerInvariant()).SetProperty(x => x.Code, code), ct);
        }
        foreach (var ((deployment, agent), status) in statuses)
        {
            var runtime = await db.Set<DeploymentRuntimeEntity>().SingleOrDefaultAsync(x => x.DeploymentId == deployment, ct);
            if (runtime is null) { runtime = new() { DeploymentId = deployment, AgentId = agent }; db.Add(runtime); }
            if (runtime.AgentId != agent) continue; // An inspection cannot silently move placement.
            var applied = rows.Where(x => x.DeploymentId == deployment && x.AgentId == agent && x.Kind == "deploy" && responses.TryGetValue(x.Id, out var r) && r.Response.Succeeded)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (applied is not null) { runtime.Revision = applied.Revision; runtime.DesiredState = "running"; }
            runtime.ObservedState = OperationWorker.Observe(status.Output); runtime.ObservedAt = clock.GetUtcNow().ToUnixTimeSeconds();
        }
        if (run is not null && run.State == "uncertain")
        {
            if (await db.Set<AutomationRun>().Where(x => x.Id == run.Id && x.State == "uncertain")
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "reconciled").SetProperty(x => x.Code, "inspected-not-retried")
                    .SetProperty(x => x.ReconciledBy, actor).SetProperty(x => x.ReconciledAt, clock.GetUtcNow().ToUnixTimeSeconds()), ct) != 1)
                throw new OperationException("Run state changed. Inspect again.");
            await db.Set<ActionDefinition>().Where(x => x.ActiveRunId == run.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveRunId, (Guid?)null), ct);
            await db.Set<ImageUpdatePolicy>().Where(x => x.ActiveRunId == run.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveRunId, (Guid?)null), ct);
        }
        await db.SaveChangesAsync(ct);
        await automation.RecordAuditAsync(actor, "execution.inspected-reconciled", (run?.Id ?? rows[0].Id).ToString(), "no-command-replay", ct);
        await transaction.CommitAsync(ct);
        return "Execution evidence and container status inspected. Operations and associated automation locks were reconciled together. No commands were retried; you may explicitly retry interrupted work.";
    }
}
