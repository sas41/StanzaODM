using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Operations;

public sealed class OperationWorker(IServiceScopeFactory scopes, IAgentCommandDispatcher dispatcher, TimeProvider clock,
    ILogger<OperationWorker> logger, InstanceMaintenance? maintenance = null) : BackgroundService
{
    private readonly Guid workerId = Guid.NewGuid();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var active = new Dictionary<Guid, Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (maintenance?.Paused == true) { await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken); continue; }
                foreach (var id in active.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToArray()) { await active[id]; active.Remove(id); }
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
                var policy = (await scope.ServiceProvider.GetRequiredService<OperationalPolicyService>().GetAsync(stoppingToken)).Policy;
                var now = clock.GetUtcNow().ToUnixTimeSeconds();
                await db.Set<OperationEntity>().Where(x => (x.State == "Running" || x.State == "CancellationRequested") && x.LeaseUntil < now)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, "Unknown").SetProperty(x => x.Code, "worker_interrupted").SetProperty(x => x.CompletedAt, now), stoppingToken);
                var queue = db.Set<OperationEntity>().AsNoTracking().Where(x => x.State == "Queued");
                if (dispatcher is IAgentConnectionCatalog catalog) { var connected = catalog.ConnectedAgentIds; queue = queue.Where(x => connected.Contains(x.AgentId)); }
                var candidates = await queue
                    .OrderBy(x => x.CreatedAt).Take(100).ToListAsync(stoppingToken);
                foreach (var candidate in candidates)
                {
                    if (active.Count >= policy.MaximumParallelOperations) break;
                    if (active.ContainsKey(candidate.Id) || !dispatcher.IsConnected(candidate.AgentId)) continue;
                    if (await ClaimAsync(candidate.Id, policy.OperationTimeoutSeconds, stoppingToken))
                        active.Add(candidate.Id, RunAsync(candidate.Id, stoppingToken));
                }
                // Retain metadata while clearing aged sensitive command output.
                var cutoff = clock.GetUtcNow().AddDays(-policy.LogRetentionDays).ToUnixTimeSeconds();
                await db.Set<OperationEntity>().Where(x => x.CompletedAt < cutoff && x.ProtectedResponse != null)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.ProtectedResponse, (string?)null), stoppingToken);
                var auditCutoff = clock.GetUtcNow().AddDays(-policy.AuditRetentionDays).ToUnixTimeSeconds();
                await db.Set<AuditEvent>().Where(x => x.At < auditCutoff).ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogError("The operation worker could not advance its durable queue."); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        try { await Task.WhenAll(active.Values); } catch (OperationCanceledException) { }
    }
    private async Task<bool> ClaimAsync(Guid id, int timeout, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        var row = await db.Set<OperationEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.State == "Queued", ct);
        if (row is null) return false;
        if (await db.Set<OperationEntity>().AnyAsync(x => x.AgentId == row.AgentId && (x.State == "Running" || x.State == "CancellationRequested"), ct)) return false;
        if (row.DeploymentId is { } deployment && await db.Set<OperationEntity>().AnyAsync(x => x.DeploymentId == deployment && (x.State == "Running" || x.State == "CancellationRequested"), ct)) return false;
        if (row.DeploymentId is { } uncertainDeployment && row.Kind is not ("status" or "logs") && await db.Set<OperationEntity>().AnyAsync(x => x.DeploymentId == uncertainDeployment && x.State == "Unknown", ct)) return false;
        var changed = await db.Set<OperationEntity>().Where(x => x.Id == id && x.State == "Queued").ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, "Running").SetProperty(x => x.WorkerId, workerId)
            .SetProperty(x => x.LeaseUntil, clock.GetUtcNow().AddSeconds(timeout + 60).ToUnixTimeSeconds()), ct);
        await transaction.CommitAsync(ct);
        return changed == 1;
    }
    private async Task RunAsync(Guid id, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<StanzaDbContext>();
        var operations = services.GetRequiredService<OperationService>();
        var row = await db.Set<OperationEntity>().AsNoTracking().SingleAsync(x => x.Id == id, stoppingToken);
        string state;
        string code;
        string? responseText = null;
        string? appliedMetadata = null;
        var dispatched = false;
        async Task Progress(Guid operationId, string stage)
        {
            if (operationId != id) return;
            try
            {
                await using var updateScope = scopes.CreateAsyncScope();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await updateScope.ServiceProvider.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().Where(x => x.Id == id && x.WorkerId == workerId && x.State == "Running")
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Stage, stage), timeout.Token);
            }
            catch (Exception) { /* Durable execution is independent of best-effort progress reporting. */ }
        }
        var reporter = dispatcher as IAgentWorkProgressSource;
        if (reporter is not null) reporter.Progress += Progress;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = MonitorCancellationAsync(id, cancel, stoppingToken);
        try
        {
            await operations.AuthorizeExecutionAsync(row, stoppingToken);
            var request = operations.Request(row);
            cancel.CancelAfter(TimeSpan.FromSeconds((request.Policy?.TimeoutSeconds ?? 300) + 15));
            dispatched = true;
            var response = await dispatcher.DispatchAsync(row.AgentId, request, cancel.Token);
            state = response.Code == "agent_busy" ? "Queued" : response.Code == "uncertain" ? "Unknown" : response.Succeeded ? "Succeeded" : response.Code == "cancelled" ? "Cancelled" : "Failed";
            code = response.Code.Length <= 100 && response.Code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? response.Code : "agent_result";
            responseText = operations.ProtectResponse(id, response);
            appliedMetadata = operations.ProtectResponse(id, response with { Output = null });
            if (response.Succeeded && row.DeploymentId is { } deployment && row.Kind is "deploy" or "start" or "stop" or "remove" or "restart" or "status")
            {
                var runtime = await db.Set<DeploymentRuntimeEntity>().SingleOrDefaultAsync(x => x.DeploymentId == deployment, stoppingToken);
                if (runtime is null) { runtime = new() { DeploymentId = deployment }; db.Set<DeploymentRuntimeEntity>().Add(runtime); }
                if (row.Kind == "deploy" || runtime.AgentId == Guid.Empty || runtime.AgentId == row.AgentId)
                {
                    runtime.AgentId = row.AgentId;
                    if (row.Kind == "deploy") runtime.Revision = row.Revision;
                    if (row.Kind != "status") runtime.DesiredState = row.Kind is "stop" or "remove" ? "stopped" : "running";
                    runtime.ObservedState = row.Kind is "status" or "deploy" && response.Output is not null ? Observe(response.Output) : runtime.DesiredState;
                    runtime.ObservedAt = clock.GetUtcNow().ToUnixTimeSeconds();
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
        }
        catch (OperationException) { state = "Failed"; code = "authorization_or_snapshot_invalid"; }
        catch (Exception) { state = dispatched ? "Unknown" : "Failed"; code = dispatched ? "execution_outcome_uncertain" : "preparation_failed"; }
        finally { if (reporter is not null) reporter.Progress -= Progress; await cancel.CancelAsync(); try { await monitor; } catch (OperationCanceledException) { } }
        // Use a separate bounded token to persist uncertainty even during host shutdown.
        using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await db.Set<OperationEntity>().Where(x => x.Id == id && x.WorkerId == workerId && (x.State == "Running" || x.State == "CancellationRequested"))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, state).SetProperty(x => x.Code, code)
                .SetProperty(x => x.ProtectedResponse, responseText).SetProperty(x => x.ProtectedAppliedMetadata, appliedMetadata).SetProperty(x => x.CompletedAt, state == "Queued" ? null : (long?)clock.GetUtcNow().ToUnixTimeSeconds()), persist.Token);
        await services.GetRequiredService<AuditService>().WriteAsync(row.ActorId, state == "Queued" ? "operation.deferred" : "operation.finished", id.ToString(), state + ":" + code, persist.Token);
    }
    private async Task MonitorCancellationAsync(Guid id, CancellationTokenSource cancel, CancellationToken stoppingToken)
    {
        while (!cancel.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), clock, cancel.Token);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
            var requested = await db.Set<OperationEntity>().AnyAsync(x => x.Id == id && x.State == "CancellationRequested", stoppingToken);
            try
            {
                var row = await db.Set<OperationEntity>().AsNoTracking().SingleAsync(x => x.Id == id, stoppingToken);
                await scope.ServiceProvider.GetRequiredService<OperationService>().AuthorizeExecutionAsync(row, stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) { requested = true; }
            if (requested) await cancel.CancelAsync();
        }
    }

    internal static string Observe(string? output)
    {
        try
        {
            var states = new List<(string State, string Health)>();
            foreach (var text in output?.TrimStart().StartsWith('[') == true ? new[] { output } : (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = System.Text.Json.JsonDocument.Parse(text!);
                var values = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
                foreach (var value in values)
                {
                    var state = value.TryGetProperty("State", out var s) ? s.GetString() : value.TryGetProperty("state", out s) ? s.GetString() : "unknown";
                    var health = value.TryGetProperty("Health", out var h) ? h.GetString() : "";
                    states.Add((state ?? "unknown", health ?? ""));
                }
            }
            if (states.Count == 0) return "absent";
            if (states.Any(x => x.Health == "unhealthy")) return "unhealthy";
            if (states.All(x => x.State == "running")) return "running";
            if (states.All(x => x.State is "exited" or "dead" or "created")) return "stopped";
            return "degraded";
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException) { return "unknown"; }
    }
}
