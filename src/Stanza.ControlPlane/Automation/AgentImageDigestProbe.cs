using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Automation;

/// <summary>Read-only authenticated image inspection of the last actually applied snapshot.
/// Original environment/files/commit identities are retained; credentials are freshly authorized.
/// No dependency on IOperationService, and no secret-reveal permission is borrowed.</summary>
public sealed class AgentImageDigestProbe(StanzaDbContext db, DeploymentDraftService drafts, AccessService access,
    ISourceSnapshotProvider sources, AgentPolicyService policies, IDataProtectionProvider protection,
    IAgentCommandDispatcher dispatcher, AuditService audit) : IImageDigestProbe
{
    public async Task<IReadOnlyList<ImageDigestObservation>> ProbeAsync(Guid actorId, Guid deploymentId,
        IReadOnlyList<string> services, CancellationToken ct = default)
    {
        var selected = ImageUpdates.ServiceNames(string.Join(',', services));
        if (selected.Count == 0 || selected.Count != services.Count) throw new AutomationException("image-probe-invalid-services");
        await RequireAsync(actorId, "automation.read", "deployment", deploymentId, ct);
        var runtime = await CurrentRuntimeAsync(deploymentId, ct);
        await RequireAsync(actorId, "agent.read", "agent", runtime.AgentId, ct);
        await ApprovedAgentAsync(runtime.AgentId, ct);
        var snapshot = await drafts.SnapshotForExecutionAsync(actorId, deploymentId, runtime.Revision, ct);
        if (snapshot.AgentId != runtime.AgentId) throw new AutomationException("image-probe-pending-placement-change");
        var applied = await db.Set<OperationEntity>().AsNoTracking().Where(x => x.DeploymentId == deploymentId && x.AgentId == runtime.AgentId
            && x.Revision == runtime.Revision && x.Kind == "deploy" && x.State == "Succeeded")
            .OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct)
            ?? throw new AutomationException("image-probe-applied-snapshot-unavailable");
        AgentWorkRequest original;
        AgentWorkResponse? deployed;
        try
        {
            var protector = protection.CreateProtector("Stanza.Operation.v1", applied.Id.ToString("N"));
            original = JsonSerializer.Deserialize<AgentWorkRequest>(protector.Unprotect(applied.ProtectedRequest))
                ?? throw new AutomationException("image-probe-applied-snapshot-invalid");
            deployed = applied.ProtectedResponse is null ? null : JsonSerializer.Deserialize<AgentWorkResponse>(protector.Unprotect(applied.ProtectedResponse));
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException)
        { throw new AutomationException("image-probe-applied-snapshot-unavailable"); }
        if (original.Id != applied.Id || original.DeploymentId != deploymentId || original.Operation != "deploy" || original.EnvironmentFiles.Count != 3)
            throw new AutomationException("image-probe-applied-snapshot-invalid");
        // PrepareAsync enforces current credential.use grants without revealing secret values.
        var fresh = await sources.PrepareAsync(actorId, deploymentId, ct);
        var pinned = PinSources(original.Sources ?? [], fresh.Git, deployed?.ResolvedSources);
        var policy = await policies.RuntimeAsync(runtime.AgentId, ct);
        if (!policy.ExecutionEnabled) throw new AutomationException("image-probe-execution-disabled");
        if (!dispatcher.IsConnected(runtime.AgentId)) throw new AgentUnavailableException();
        var request = original with { Id = Guid.NewGuid(), Operation = "image-digests", Execution = null, Sources = pinned,
            Registries = fresh.Registries.ToArray(), Policy = policy };
        // There is no browser-provided target or executable in this request. All material comes
        // from the applied operation and freshly authorized credential references.
        await RequireAsync(actorId, "automation.read", "deployment", deploymentId, ct);
        await RequireAsync(actorId, "agent.read", "agent", runtime.AgentId, ct);
        await audit.WriteAsync(actorId, "image-probe.started", deploymentId.ToString(), "read-only", ct);
        var response = await dispatcher.DispatchAsync(runtime.AgentId, request, ct);
        await RequireAsync(actorId, "automation.read", "deployment", deploymentId, ct);
        await RequireAsync(actorId, "agent.read", "agent", runtime.AgentId, ct);
        await ApprovedAgentAsync(runtime.AgentId, ct);
        var after = await CurrentRuntimeAsync(deploymentId, ct);
        if (after.AgentId != runtime.AgentId || after.Revision != runtime.Revision) throw new AutomationException("image-probe-applied-state-changed");
        // Recheck scoped credential access after a potentially long registry query as well.
        await sources.PrepareAsync(actorId, deploymentId, ct);
        if (response.Id != request.Id || !response.Succeeded || response.Output is null || response.Output.Length > policy.MaximumOutputCharacters)
        {
            await audit.WriteAsync(actorId, "image-probe.finished", deploymentId.ToString(), "failed", ct);
            throw new AutomationException("image-probe-agent-failed");
        }
        AgentImageObservation[] observations;
        try { observations = JsonSerializer.Deserialize<AgentImageObservation[]>(response.Output, AutomationRules.Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new AutomationException("image-probe-invalid-response"); }
        if (observations.Length > 100 || observations.Any(x => x is null) || observations.Select(x => x.Service).Distinct(StringComparer.Ordinal).Count() != observations.Length)
            throw new AutomationException("image-probe-invalid-response");
        var result = observations.Where(x => selected.Contains(x.Service, StringComparer.Ordinal)).Select(x =>
            new ImageDigestObservation(x.Service, x.Image, x.Platform, x.CurrentDigest, x.RemoteDigest, x.LocallyBuilt, x.ExplicitRegistrySource)).ToArray();
        ImageUpdates.Changed(result, selected); // Missing/unknown selected services fail, never fake equality.
        await audit.WriteAsync(actorId, "image-probe.finished", deploymentId.ToString(), "succeeded", ct);
        return result;
    }

    private async Task<DeploymentRuntimeEntity> CurrentRuntimeAsync(Guid deployment, CancellationToken ct)
    {
        var runtime = await db.Set<DeploymentRuntimeEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment, ct)
            ?? throw new AutomationException("image-probe-not-deployed");
        var latest = await db.Set<DeploymentDraftEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == deployment, ct)
            ?? throw new AutomationException("image-probe-deployment-unavailable");
        if (latest.Revision != runtime.Revision || latest.TargetAgentId != runtime.AgentId) throw new AutomationException("image-probe-pending-manual-changes");
        if (await db.Set<OperationEntity>().AnyAsync(x => x.DeploymentId == deployment && (x.State == "Queued" || x.State == "Running" || x.State == "CancellationRequested" || x.State == "Unknown"), ct))
            throw new AutomationException("image-probe-deployment-operation-pending");
        return runtime;
    }

    private async Task ApprovedAgentAsync(Guid agent, CancellationToken ct)
    {
        if (!await db.Set<AgentEntity>().AsNoTracking().AnyAsync(x => x.Id == agent && x.Status == "approved", ct)) throw new AutomationAccessException();
    }
    private async Task RequireAsync(Guid actor, string permission, string type, Guid id, CancellationToken ct)
    { if (!await access.IsAllowedAsync(actor, permission, type, id, ct)) throw new AutomationAccessException(); }

    private static IReadOnlyList<AgentGitSource> PinSources(IReadOnlyList<AgentGitSource> original, IReadOnlyList<AgentGitSource> fresh,
        IReadOnlyDictionary<string, string>? resolved)
    {
        if (original.Count != fresh.Count) throw new AutomationException("image-probe-pending-source-changes");
        var result = new List<AgentGitSource>();
        foreach (var source in original)
        {
            var current = fresh.SingleOrDefault(x => x.Destination == source.Destination && x.Url == source.Url && x.Reference == source.Reference)
                ?? throw new AutomationException("image-probe-pending-source-changes");
            if (resolved?.TryGetValue(source.Destination, out var commit) != true || commit.Length != 40 || !commit.All(Uri.IsHexDigit))
                throw new AutomationException("image-probe-applied-commit-unavailable");
            result.Add(current with { Reference = commit });
        }
        return result;
    }
}
