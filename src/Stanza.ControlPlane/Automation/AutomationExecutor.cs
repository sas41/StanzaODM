using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Automation;

public sealed class AutomationExecutor(StanzaDbContext db, AutomationService automation, AutomationAuthorization authorization,
    IOperationService operations, IImageDigestProbe imageProbe, TimeProvider clock, IServiceScopeFactory? scopes = null)
{
    private readonly Guid workerId = Guid.NewGuid();

    public async Task<bool> ExecuteNextAsync(CancellationToken ct = default)
    {
        var candidates = await db.Set<AutomationRun>().AsNoTracking().Where(x => x.State == "queued").OrderBy(x => x.CreatedAt).Take(100).ToListAsync(ct);
        foreach (var run in candidates)
        {
            if (!await ClaimAsync(run, ct)) continue;
            await ExecuteAsync(run, ct); return true;
        }
        return false;
    }

    public async Task<bool> ClaimAsync(AutomationRun run, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.Set<AutomationRun>().Where(x => x.Id == run.Id && x.State == "queued")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "running").SetProperty(x => x.LeaseOwner, workerId)
                .SetProperty(x => x.LeaseUntil, automation.Now + 120), ct) != 1) return false;
        if (run.ActionId is { } actionId)
        {
            var action = await automation.ActionAsync(actionId, ct);
            if (await db.Set<ActionDefinition>().Where(x => x.Id == actionId && x.ActiveRunId == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveRunId, run.Id), ct) != 1)
            {
                if (action.Concurrency == "reject")
                {
                    await FinishAsync(run, "skipped", "action-concurrency", ct);
                    await transaction.CommitAsync(ct);
                }
                // Queue policy rolls back the run claim and tries another independent target.
                return false;
            }
        }
        if (run.Operation == "image-check" && await db.Set<ImageUpdatePolicy>().Where(x => x.Id == run.DefinitionId && x.ActiveRunId == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveRunId, run.Id), ct) != 1)
        {
            await FinishAsync(run, "skipped", "image-check-concurrency", ct); await transaction.CommitAsync(ct); return false;
        }
        await transaction.CommitAsync(ct); run.State = "running"; run.LeaseOwner = workerId; return true;
    }

    private async Task ExecuteAsync(AutomationRun run, CancellationToken ct)
    {
        try
        {
            await CheckIdentityAsync(run, ct);
            if (run.Operation == "image-check") { await CheckImagesAsync(run, ct); return; }
            var plan = automation.Unprotect<ActionPlan>(run.ProtectedPlan);
            var parameters = automation.Unprotect<Dictionary<string, string>>(run.ProtectedParameters);
            AutomationRules.Validate(plan); parameters = new(AutomationRules.Parameters(plan, parameters));
            var failed = false;
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                await CheckIdentityAsync(run, ct);
                var step = AutomationRules.Resolve(plan.Steps[i], parameters);
                await AuthorizeStepAsync(run, step, ct);
                var result = new AutomationStepRun { RunId = run.Id, Index = i, DeploymentId = step.DeploymentId, AgentId = step.AgentId,
                    Kind = step.Kind, State = "running" };
                db.Add(result); await db.SaveChangesAsync(ct); db.Entry(result).State = EntityState.Detached;
                // The durable running step precedes enqueue. A crash in the enqueue/record gap is uncertain,
                // never permission to repeat a host command or deployment.
                var bindings = automation.ExecutionBindings(run, plan, step, parameters);
                var operation = await operations.EnqueueAsync(new(ExecutionActor(run), step.DeploymentId, AutomationRules.OperationKind(step.Kind), "automation:" + run.Id.ToString("N"),
                    step.AgentId, step.Execution, bindings.Sources, bindings.Images), ct);
                run.OperationId = operation.Id;
                await db.Set<AutomationRun>().Where(x => x.Id == run.Id && x.LeaseOwner == workerId)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.OperationId, operation.Id), ct);
                await db.Set<AutomationStepRun>().Where(x => x.RunId == run.Id && x.Index == i)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.OperationId, operation.Id).SetProperty(x => x.AgentId, operation.AgentId), ct);
                await automation.RecordAuditAsync(run.OwnerId, "automation.target.resolved", $"{run.Id}:{i}:{operation.AgentId}", step.Kind, ct);
                var completed = await WaitAsync(run, operation.Id, step.TimeoutSeconds, ct);
                var operationState = completed.State.ToLowerInvariant();
                var succeeded = operationState == "succeeded";
                await db.Set<AutomationStepRun>().Where(x => x.RunId == run.Id && x.Index == i)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, operationState).SetProperty(x => x.Code, succeeded ? "completed" : "operation-failed"), ct);
                if (!succeeded)
                {
                    failed = true;
                    if (operationState is "unknown" or "uncertain") { await FinishAsync(run, "uncertain", "operation-outcome-unknown", ct); return; }
                    if (operationState == "cancelled") { await FinishAsync(run, "cancelled", "operation-cancelled", ct); return; }
                    if (step.Failure == "stop") break;
                }
            }
            await FinishAsync(run, failed ? "failed" : "succeeded", failed ? "step-failed" : "completed", ct);
        }
        catch (AutomationAccessException) { await FinishAsync(run, run.OperationId is null ? "denied" : "uncertain", "access-revoked", CancellationToken.None); }
        catch (AutomationRunCancelledException) { await FinishAsync(run, run.OperationId is null ? "cancelled" : "uncertain", "cancel-requested-awaiting-reconciliation", CancellationToken.None); }
        catch (AutomationTimeoutException) { await FinishAsync(run, "uncertain", "step-timeout-cancellation-requested", CancellationToken.None); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful host shutdown is also an uncertain outcome. Retain locks until explicitly reconciled.
            await FinishAsync(run, "uncertain", "worker-stopped-no-replay", CancellationToken.None);
        }
        catch (Exception)
        {
            // An exception after dispatch cannot prove that no side effects occurred.
            await FinishAsync(run, "uncertain", "execution-error-reconcile-before-retry", CancellationToken.None);
        }
    }

    private async Task CheckIdentityAsync(AutomationRun run, CancellationToken ct)
    {
        var current = await db.Set<AutomationRun>().AsNoTracking().SingleAsync(x => x.Id == run.Id, ct);
        if (current.CancelRequested) throw new AutomationRunCancelledException();
        if (current.State != "running" || current.LeaseOwner != workerId) throw new AutomationException("Execution lease lost.");
        if (run.ActionId is { } actionId)
        {
            var action = await automation.ActionAsync(actionId, ct);
            if (!action.Enabled || action.OwnerId != run.ActionOwnerId) throw new AutomationAccessException();
            await authorization.RequireAsync(run.OwnerId, "action.execute", "action", actionId, ct);
            await authorization.RequireAsync(action.OwnerId, "action.execute", "action", actionId, ct);
            if (run.ActionOwnerId is { } owner) await authorization.RequireAsync(owner, "action.execute", "action", actionId, ct);
        }
        if (run.Source == "schedule")
        {
            var definition = await db.Set<AutomationSchedule>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == run.DefinitionId, ct);
            if (definition is null || !definition.Enabled || definition.OwnerId != run.OwnerId || definition.ActionId != run.ActionId || definition.DeploymentId != run.DeploymentId)
                throw new AutomationAccessException();
            await authorization.ScheduleAsync(run.OwnerId, definition.ActionId, definition.DeploymentId, ct);
        }
        if (run.Source == "webhook")
        {
            var definition = await db.Set<WebhookDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == run.DefinitionId, ct);
            if (definition is null || !definition.Enabled || definition.ProtectedSecret.Length == 0 || definition.OwnerId != run.OwnerId) throw new AutomationAccessException();
        }
        if (run.Source is "image-policy" or "manual-image-policy")
        {
            var definition = await db.Set<ImageUpdatePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == run.DefinitionId, ct);
            if (definition is null || (run.Source == "image-policy" && !definition.Enabled) || definition.OwnerId != run.OwnerId) throw new AutomationAccessException();
            if (ImagePolicySnapshot.From(definition) != automation.Unprotect<ImagePolicySnapshot>(run.ProtectedPlan)) throw new AutomationAccessException();
            await authorization.RequireAsync(run.RequestedBy, "automation.manage", "deployment", definition.DeploymentId, ct);
            await authorization.RequireAsync(run.RequestedBy, "deployment.read", "deployment", definition.DeploymentId, ct);
            await authorization.RequireAsync(run.OwnerId, "deployment.read", "deployment", definition.DeploymentId, ct);
            if (definition.Mode == "auto-deploy") await authorization.RequireAsync(run.RequestedBy, "deployment.deploy", "deployment", definition.DeploymentId, ct);
            if (definition.Mode == "auto-deploy") await authorization.RequireAsync(run.OwnerId, "deployment.deploy", "deployment", definition.DeploymentId, ct);
        }
        if (run.Source is "webhook" or "image-policy" or "manual-image-policy")
            await authorization.RequireAsync(run.OwnerId, "automation.manage", "deployment", run.DeploymentId, ct);
    }

    private async Task AuthorizeStepAsync(AutomationRun run, ActionStep step, CancellationToken ct)
    {
        await authorization.TargetAsync(ExecutionActor(run), step, ct);
        if (run.ActionId is { } id)
        {
            await authorization.TargetAsync((await automation.ActionAsync(id, ct)).OwnerId, step, ct);
            if (run.ActionOwnerId is { } owner) await authorization.TargetAsync(owner, step, ct);
        }
    }

    private static Guid ExecutionActor(AutomationRun run) => run.ActionId.HasValue
        ? run.ActionOwnerId ?? throw new AutomationAccessException() : run.OwnerId;

    private async Task<OperationSummary> WaitAsync(AutomationRun run, Guid operationId, int seconds, CancellationToken ct)
    {
        // WaitAsync polls persistence. A separate DI scope prevents concurrent use of the same EF
        // context by the operation waiter and our access/lease/cancellation heartbeat.
        using var scope = scopes?.CreateScope();
        var waitService = scope?.ServiceProvider.GetRequiredService<IOperationService>() ?? operations;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var wait = waitService.WaitAsync(ExecutionActor(run), operationId, linked.Token);
        try
        {
            while (!wait.IsCompleted)
            {
                await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(2), clock, linked.Token));
                linked.Token.ThrowIfCancellationRequested();
                await CheckIdentityAsync(run, ct);
                // Recheck the currently running target on every heartbeat, not just when the next step starts.
                var step = await db.Set<AutomationStepRun>().AsNoTracking().Where(x => x.RunId == run.Id && x.OperationId == operationId).SingleOrDefaultAsync(ct);
                if (step is not null) await AuthorizeStepAsync(run, new(step.Kind, step.DeploymentId, step.Kind == "host-exec" ? step.AgentId : null), ct);
                await db.Set<AutomationRun>().Where(x => x.Id == run.Id && x.State == "running" && x.LeaseOwner == workerId)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.LeaseUntil, automation.Now + 120), ct);
            }
            return await wait;
        }
        catch
        {
            linked.Cancel();
            try { await operations.CancelAsync(ExecutionActor(run), operationId, CancellationToken.None); } catch { /* Revoked identity is not elevated for cancellation. */ }
            try { await wait; } catch { /* Observe the cancelled wait. */ }
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested) throw new AutomationTimeoutException();
            throw;
        }
    }

    private async Task CheckImagesAsync(AutomationRun run, CancellationToken ct)
    {
        var policy = await db.Set<ImageUpdatePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == run.DefinitionId, ct)
            ?? throw new AutomationException("Image policy is unavailable.");
        if (policy.OwnerId != run.OwnerId) throw new AutomationAccessException();
        await authorization.RequireAsync(run.OwnerId, "deployment.read", "deployment", policy.DeploymentId, ct);
        if (policy.Mode == "auto-deploy") await authorization.RequireAsync(run.OwnerId, "deployment.deploy", "deployment", policy.DeploymentId, ct);
        IReadOnlyList<ImageDigestObservation> observations;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            observations = await imageProbe.ProbeAsync(run.OwnerId, policy.DeploymentId, ImageUpdates.ServiceNames(policy.Services), linked.Token);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            var failure = ImageUpdates.ProbeFailureCode(error);
            await UpdatePolicyResultAsync(policy, failure, null, ct, failed: true);
            await FinishAsync(run, "failed", failure, ct); return;
        }
        var changed = ImageUpdates.Changed(observations, ImageUpdates.ServiceNames(policy.Services));
        await CheckIdentityAsync(run, ct);
        var candidates = observations.Where(o => !o.LocallyBuilt || o.ExplicitRegistrySource).ToArray();
        var unknownBaseline = candidates.Any(o => o.CurrentDigest is null);
        var code = candidates.Length == 0 ? "no-registry-candidates"
            : unknownBaseline ? changed.Count == 0 ? "baseline-unknown" : "updates-available-baseline-unknown"
            : changed.Count == 0 ? "unchanged" : "updates-available";
        await UpdatePolicyResultAsync(policy, code, observations, ct);
        if (changed.Count > 0 && !unknownBaseline && policy.Mode == "auto-deploy")
        {
            await authorization.TargetAsync(run.OwnerId, new("deploy", policy.DeploymentId), ct);
            var operation = await operations.EnqueueAsync(new(run.OwnerId, policy.DeploymentId, "deploy", "automation:" + run.Id.ToString("N")), ct);
            run.OperationId = operation.Id;
            await db.Set<AutomationRun>().Where(x => x.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.OperationId, operation.Id), ct);
            var completed = await WaitAsync(run, operation.Id, 3600, ct);
            var operationState = completed.State.ToLowerInvariant();
            code = operationState == "succeeded" ? "redeployed-health-verified" : "redeploy-failed";
            await UpdatePolicyResultAsync(policy, code, observations, ct);
            await FinishAsync(run, operationState == "succeeded" ? "succeeded" : operationState is "unknown" or "uncertain" ? "uncertain" : "failed", code, ct); return;
        }
        await FinishAsync(run, "succeeded", code, ct);
    }

    private Task<int> UpdatePolicyResultAsync(ImageUpdatePolicy policy, string code, IReadOnlyList<ImageDigestObservation>? observations, CancellationToken ct, bool failed = false)
    {
        var json = observations is null ? policy.DigestsJson : JsonSerializer.Serialize(observations, AutomationRules.Json);
        var next = automation.Now + (failed || code.Contains("failed", StringComparison.Ordinal) ? policy.FailureBackoffMinutes : policy.FrequencyMinutes) * 60L;
        return db.Set<ImageUpdatePolicy>().Where(x => x.Id == policy.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastCheckAt, automation.Now)
            .SetProperty(x => x.LastResult, code).SetProperty(x => x.DigestsJson, json).SetProperty(x => x.NextRunAt, next), ct);
    }

    private async Task FinishAsync(AutomationRun run, string state, string code, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (await db.Set<AutomationRun>().Where(x => x.Id == run.Id && x.State == "running" && x.LeaseOwner == workerId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, state).SetProperty(x => x.Code, code).SetProperty(x => x.CompletedAt, automation.Now), ct) != 1) return;
        await db.Set<AutomationStepRun>().Where(x => x.RunId == run.Id && x.State == "running")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, state).SetProperty(x => x.Code, code), ct);
        if (state != "uncertain")
        {
            await db.Set<ActionDefinition>().Where(x => x.ActiveRunId == run.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveRunId, (Guid?)null), ct);
            await db.Set<ImageUpdatePolicy>().Where(x => x.ActiveRunId == run.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveRunId, (Guid?)null), ct);
        }
        await automation.RecordAuditAsync(run.OwnerId, "automation.completed", run.Id.ToString(), state, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private sealed class AutomationRunCancelledException : Exception;
    private sealed class AutomationTimeoutException : Exception;
}
