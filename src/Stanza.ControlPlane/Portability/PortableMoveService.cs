using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Portability;

/// <summary>Health must be evidence from the destination operation, not HTTP dispatch success.</summary>
public interface IPortableMoveHealthVerifier
{
    Task<bool> IsHealthyAsync(OperationSummary destinationStatus, CancellationToken ct);
}

public sealed class PortableMoveHealthVerifier(OperationService? operations = null) : IPortableMoveHealthVerifier
{
    public async Task<bool> IsHealthyAsync(OperationSummary destinationStatus, CancellationToken ct)
    {
        if (destinationStatus.Kind != "status" || !destinationStatus.State.Equals("succeeded", StringComparison.OrdinalIgnoreCase)) return false;
        // A runtime may provide an explicit normalized health code. Otherwise inspect the actual Compose status JSON.
        if (destinationStatus.Code == "healthy") return true;
        if (operations is null) return false;
        var detail = await operations.DetailAsync(destinationStatus.ActorId, destinationStatus.Id, ct);
        return RuntimeStatusHealthy(detail.Output);
    }

    public static bool RuntimeStatusHealthy(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || output.Length > 65536) return false;
        try
        {
            var values = new List<JsonElement>();
            if (output.TrimStart().StartsWith('['))
            {
                using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 16 });
                values.AddRange(document.RootElement.EnumerateArray().Select(x => x.Clone()));
            }
            else
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
                    values.Add(document.RootElement.Clone());
                }
            }
            if (values.Count is 0 or > 256) return false;
            return values.All(value => value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("State", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "running"
                && value.TryGetProperty("Service", out var service) && service.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(service.GetString())
                && value.TryGetProperty("Health", out var health) && health.ValueKind == JsonValueKind.String && health.GetString() is "" or "healthy");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }
}

public sealed class PortableMoveService(StanzaDbContext db, DeploymentDraftService drafts, PortabilityService portability,
    IPortableDraftStore store, IOperationService operations, IPortableMoveHealthVerifier health, TimeProvider clock,
    OperationService? details = null)
{
    public async Task<Guid> QueueAsync(Guid actor, Guid deploymentId, Guid destination, bool storageAcknowledged, CancellationToken ct = default)
    {
        if (!storageAcknowledged) throw new PortabilityException("Confirm that no persistent data will be transferred and source data will not be deleted.");
        await AuthorizeAsync(actor, deploymentId, destination, ct);
        var deployment = await drafts.ReadAsync(actor, deploymentId, ct: ct);
        if (deployment.Draft.TargetAgentId is not { } source || source == destination)
            throw new PortabilityException("Select a different destination for an assigned deployment.");
        await portability.ValidateAgentAsync(actor, source, ct);
        // Serialize creation across processes using the existing database security write guard.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Security.SecurityWriteGuard.LockAsync(db, ct);
        if (await db.Set<PortableMoveEntity>().AnyAsync(x => x.DeploymentId == deploymentId && x.State != "complete" && x.State != "cleaned" && x.State != "cancelled", ct))
            throw new PortabilityException("This deployment already has a move or recovery record. Inspect it before creating another move.");
        var move = new PortableMoveEntity { Id = Guid.NewGuid(), ActorId = actor, DeploymentId = deploymentId,
            SourceAgentId = source, DestinationAgentId = destination, Revision = deployment.Draft.Revision,
            CreatedAt = clock.GetUtcNow().ToUnixTimeSeconds(), Warnings = JsonSerializer.Serialize(new[] {
                "Architecture compatibility is not established: transport capability reporting is unavailable. Review Compose platform declarations and the destination host before proceeding.",
                "Destination status inspection covers this deployment's Compose project only, not all host ports, names, mounts, networks or volumes. Conflicts with other workloads remain possible." }
                .Concat(PortableBundleValidator.Warnings(deployment.ComposeText ?? ""))) };
        db.Add(move);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        finally { db.Entry(move).State = EntityState.Detached; }
        return move.Id;
    }

    public async Task<PortableMoveEntity> ReadAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        var move = await db.Set<PortableMoveEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new PortabilityException("That move is unavailable.");
        await portability.RequireAsync(actor, "deployment.read", "deployment", move.DeploymentId, ct);
        await portability.RequireAsync(actor, "deployment.move", "deployment", move.DeploymentId, ct);
        return move;
    }

    public async Task AdvanceAsync(Guid id, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var lease = now + 600;
        var claimed = await db.Set<PortableMoveEntity>().Where(x => x.Id == id && x.LeaseUntil <= now
            && x.State != "complete" && x.State != "cleaned" && x.State != "recovery" && x.State != "cancelled")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LeaseUntil, lease), ct);
        if (claimed != 1) return;
        var move = await db.Set<PortableMoveEntity>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
        try
        {
            await AuthorizeAsync(move.ActorId, move.DeploymentId, move.DestinationAgentId, ct);
            await portability.ValidateAgentAsync(move.ActorId, move.SourceAgentId, ct);
            var draft = await drafts.ReadAsync(move.ActorId, move.DeploymentId, ct: ct);
            if (move.State is "cleanup" or "submitting-cleanup")
            {
                await portability.RequireAsync(move.ActorId, "deployment.remove", "deployment", move.DeploymentId, ct);
                if (draft.Draft.Revision != move.Revision + 1 || draft.Draft.TargetAgentId != move.DestinationAgentId || move.State == "submitting-cleanup")
                    throw new PortabilityException("Placement changed or cleanup dispatch is uncertain; inspect operations before manual recovery.");
                var cleanupId = move.OperationId;
                if (cleanupId is null)
                {
                    await UpdateAsync(id, "submitting-cleanup", null, null, ct);
                    var operation = await operations.EnqueueAsync(new(move.ActorId, move.DeploymentId, "remove", "move-cleanup:" + id.ToString("N"), move.SourceAgentId), ct);
                    cleanupId = operation.Id;
                    await UpdateAsync(id, "cleanup", cleanupId, null, ct);
                }
                using var cleanupTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cleanupTimeout.CancelAfter(TimeSpan.FromMinutes(5));
                var cleanup = await operations.WaitAsync(move.ActorId, cleanupId.Value, cleanupTimeout.Token);
                if (!cleanup.State.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || cleanup.AgentId != move.SourceAgentId || cleanup.Kind != "remove")
                    throw new PortabilityException("Source cleanup did not complete; inspect its operation before retrying anything.");
                await UpdateAsync(id, "cleaned", cleanupId, null, ct);
                return;
            }
            if (draft.Draft.Revision != move.Revision || draft.Draft.TargetAgentId != move.SourceAgentId)
                throw new PortabilityException("Saved configuration or placement changed during the move; manual reconciliation is required.");
            if (move.State.StartsWith("submitting-", StringComparison.Ordinal))
                throw new PortabilityException("A submission was interrupted with unknown dispatch outcome. Inspect durable operations before manual recovery; no command was repeated.");
            if (move.State == "placement")
            {
                await store.ReassignAsync(move.ActorId, move.DeploymentId, move.Revision, move.DestinationAgentId, ct);
                await UpdateAsync(id, "complete", null, null, ct);
                return;
            }
            var kind = move.State switch
            {
                "validate" => "validate", "inspect-destination" => "status", "stop-source" => "stop", "start-destination" => "deploy", "health" => "status",
                _ => throw new PortabilityException("Unknown durable move phase; manual reconciliation is required.")
            };
            var target = move.State == "stop-source" ? move.SourceAgentId : move.DestinationAgentId;
            Guid operationId;
            if (move.OperationId is { } existing) operationId = existing;
            else
            {
                // Persist the uncertain boundary BEFORE enqueue. A crash here is recoverable, never blindly replayed.
                await UpdateAsync(id, "submitting-" + move.State, null, null, ct);
                var operation = await operations.EnqueueAsync(new(move.ActorId, move.DeploymentId, kind, "move:" + id.ToString("N"), target), ct);
                operationId = operation.Id;
                await UpdateAsync(id, move.State, operationId, null, ct);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var result = await operations.WaitAsync(move.ActorId, operationId, timeout.Token);
            if (!result.State.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || result.AgentId != target
                || result.DeploymentId != move.DeploymentId || result.ActorId != move.ActorId || result.Kind != kind)
                throw new PortabilityException("An Agent operation failed or has an unknown result; inspect operation history and reconcile before recovery.");
            if (move.State == "health" && !await health.IsHealthyAsync(result, ct))
                throw new PortabilityException("Destination health has not been proven by runtime status. Placement remains unchanged; reconcile the destination workload manually.");
            if (move.State == "inspect-destination")
            {
                var warnings = JsonSerializer.Deserialize<List<string>>(move.Warnings) ?? [];
                var output = details is null ? null : (await details.DetailAsync(move.ActorId, result.Id, ct)).Output;
                warnings.Add(string.IsNullOrWhiteSpace(output) || output.Trim() == "[]"
                    ? "Destination project status returned no visible containers or no inspectable output. This does not prove the host has no conflicts."
                    : "Destination already reports runtime status for this project. Review the correlated status operation for existing containers and conflicts before considering source cleanup. No existing host data is transferred or deleted.");
                await db.Set<PortableMoveEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Warnings, JsonSerializer.Serialize(warnings)), ct);
            }
            var next = move.State switch { "validate" => "inspect-destination", "inspect-destination" => "stop-source", "stop-source" => "start-destination", "start-destination" => "health", _ => "placement" };
            await UpdateAsync(id, next, null, null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Leave operation ID/phase durable; the next worker waits for the SAME operation after lease expiry.
            throw;
        }
        catch (Exception)
        {
            await db.Set<PortableMoveEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "recovery")
                .SetProperty(x => x.RecoveryAt, clock.GetUtcNow().ToUnixTimeSeconds())
                .SetProperty(x => x.Failure, "Move paused for recovery. Source or destination may be running; inspect correlated operations. Placement was not intentionally changed without proven destination health. No persistent data was transferred or deleted."), CancellationToken.None);
        }
        finally
        {
            await db.Set<PortableMoveEntity>().Where(x => x.Id == id && x.LeaseUntil == lease)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.LeaseUntil, 0L), CancellationToken.None);
        }
    }

    public async Task QueueSourceCleanupAsync(Guid actor, Guid id, bool acknowledged, CancellationToken ct = default)
    {
        if (!acknowledged) throw new PortabilityException("Explicitly acknowledge removing stopped source containers/networks without deleting volumes, bind mounts or managed files.");
        var move = await ReadAsync(actor, id, ct);
        if (move.ActorId != actor) throw new PortabilityAccessException();
        await AuthorizeAsync(actor, move.DeploymentId, move.DestinationAgentId, ct);
        await portability.ValidateAgentAsync(actor, move.SourceAgentId, ct);
        await portability.RequireAsync(actor, "deployment.remove", "deployment", move.DeploymentId, ct);
        var draft = await drafts.ReadAsync(actor, move.DeploymentId, ct: ct);
        if (draft.Draft.TargetAgentId != move.DestinationAgentId || draft.ViewedRevision != move.Revision + 1)
            throw new PortabilityException("Configuration changed after this move. Inspect and clean up the old source explicitly using operation tools instead.");
        var claimed = await db.Set<PortableMoveEntity>().Where(x => x.Id == id && x.State == "complete")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "cleanup").SetProperty(x => x.OperationId, (Guid?)null), ct);
        if (claimed != 1) throw new PortabilityException("Source cleanup is already queued or this move is not complete.");
    }

    public async Task<Guid> QueueRecoveryInspectionAsync(Guid actor, Guid id, bool destination, CancellationToken ct = default)
    {
        var move = await ReadAsync(actor, id, ct);
        if (move.State != "recovery") throw new PortabilityException("Only a paused recovery record needs recovery inspection.");
        await AuthorizeAsync(actor, move.DeploymentId, move.DestinationAgentId, ct);
        await portability.ValidateAgentAsync(actor, move.SourceAgentId, ct);
        var operation = await operations.EnqueueAsync(new(actor, move.DeploymentId, "status", "move-recovery:" + id.ToString("N"),
            destination ? move.DestinationAgentId : move.SourceAgentId), ct);
        return operation.Id;
    }

    public async Task CloseRecoveryAsync(Guid actor, Guid id, bool acknowledged, CancellationToken ct = default)
    {
        if (!acknowledged) throw new PortabilityException("Explicitly acknowledge observed source/destination state and that closing recovery neither changes placement nor repeats commands.");
        var move = await ReadAsync(actor, id, ct);
        await AuthorizeAsync(actor, move.DeploymentId, move.DestinationAgentId, ct);
        await portability.ValidateAgentAsync(actor, move.SourceAgentId, ct);
        if (move.State != "recovery" || move.RecoveryAt is null) throw new PortabilityException("This record is not paused for recovery.");
        var trigger = "move-recovery:" + id.ToString("N");
        foreach (var agent in new[] { move.SourceAgentId, move.DestinationAgentId })
            if (!await db.Set<OperationEntity>().AnyAsync(x => x.DeploymentId == move.DeploymentId && x.AgentId == agent
                && x.ActorId == actor && x.Trigger == trigger && x.Kind == "status" && x.State == "Succeeded"
                && x.CreatedAt >= move.RecoveryAt && x.CreatedAt >= clock.GetUtcNow().AddMinutes(-10).ToUnixTimeSeconds(), ct))
                throw new PortabilityException("Obtain fresh successful runtime status on both source and destination after the failure, inspect the results, and reconcile workloads before closing recovery.");
        var changed = await db.Set<PortableMoveEntity>().Where(x => x.Id == id && x.State == "recovery")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "cancelled")
                .SetProperty(x => x.Failure, "Recovery was explicitly closed after fresh source/destination inspections. Placement was left unchanged; no command was replayed. Inspect workload state before starting another move."), ct);
        if (changed != 1) throw new PortabilityException("Recovery state changed; reload before proceeding.");
    }

    private async Task AuthorizeAsync(Guid actor, Guid deploymentId, Guid destination, CancellationToken ct)
    {
        foreach (var action in new[] { "deployment.read", "deployment.move", "deployment.validate", "deployment.stop", "deployment.deploy", "files.read", "files.write", "operations.read", "logs.read" })
            await portability.RequireAsync(actor, action, "deployment", deploymentId, ct);
        await portability.ValidateAgentAsync(actor, destination, ct);
    }
    private Task<int> UpdateAsync(Guid id, string state, Guid? operation, string? failure, CancellationToken ct) =>
        db.Set<PortableMoveEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, state)
            .SetProperty(x => x.OperationId, operation).SetProperty(x => x.Failure, failure), ct);
}

public sealed class PortableMoveWorker(IServiceScopeFactory scopes, ILogger<PortableMoveWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
                var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds();
                await db.Set<PortableImportEntity>().Where(x => x.State == "preview" && x.ExpiresAt <= now)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "expired").SetProperty(x => x.ProtectedContent, Array.Empty<byte>()), stoppingToken);
                var ids = await db.Set<PortableMoveEntity>().AsNoTracking()
                    .Where(x => x.State != "complete" && x.State != "cleaned" && x.State != "recovery" && x.State != "cancelled")
                    .OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(10).ToListAsync(stoppingToken);
                foreach (var id in ids)
                    await scope.ServiceProvider.GetRequiredService<PortableMoveService>().AdvanceAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Portability move worker could not advance; durable state is retained for a later pass."); }
        }
    }
}
