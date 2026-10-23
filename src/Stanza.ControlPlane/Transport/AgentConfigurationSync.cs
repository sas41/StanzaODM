using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Transport;

public sealed record AgentConfigurationSnapshot(AgentConfiguration Configuration,
    IReadOnlyList<AgentDeploymentConfiguration> Deployments, int CheckIntervalSeconds);

public sealed class AgentConfigurationSync(StanzaDbContext db, IDataProtectionProvider protection,
    InstanceSettingsService settings, AgentPolicyService policies, OperationalPolicyService operational)
{
    // No browser actor is impersonated here: the channel has authenticated and approved this Agent.
    // Only configuration assigned to that exact Agent is decrypted; credentials are never replicated.
    public async Task<AgentConfigurationSnapshot> ReadAsync(Guid agentId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var agent = await db.Set<AgentEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == agentId && x.Status == "approved", ct)
            ?? throw new IOException("Agent configuration is unavailable.");
        var global = await settings.ReadInternalAsync(ct);
        var environment = string.IsNullOrEmpty(agent.ProtectedEnvironment) ? "" : protection
            .CreateProtector("Stanza.AgentEnvironment.v1", agentId.ToString("N")).Unprotect(agent.ProtectedEnvironment);
        var assigned = await db.Set<DeploymentDraftEntity>().AsNoTracking().Where(x => x.TargetAgentId == agentId).OrderBy(x => x.Id).ToListAsync(ct);
        var deployments = new List<AgentDeploymentConfiguration>();
        foreach (var draft in assigned)
        {
            var revision = await db.Set<DeploymentDraftRevisionEntity>().AsNoTracking()
                .SingleAsync(x => x.DeploymentId == draft.Id && x.Number == draft.Revision, ct);
            var snapshot = DeploymentDraftService.DecodeForSynchronization(protection, draft.Id, draft.Revision, revision.ProtectedContent);
            var sources = await db.Set<DeploymentGitSourceEntity>().AsNoTracking().Where(x => x.DeploymentId == draft.Id)
                .OrderBy(x => x.Position).ThenBy(x => x.Id)
                .Select(x => new AgentSourceDefinition(x.Url, x.Reference, x.Destination, x.CredentialId)).ToArrayAsync(ct);
            var registries = await db.Set<DeploymentRegistryBindingEntity>().AsNoTracking().Where(x => x.DeploymentId == draft.Id)
                .OrderBy(x => x.CredentialId).Select(x => x.CredentialId).ToArrayAsync(ct);
            deployments.Add(new(draft.Id, snapshot.Name, snapshot.Revision, snapshot.Files, snapshot.EnvironmentText, sources, registries));
        }
        var configuration = new AgentConfiguration(agentId, global.GlobalEnvironment, environment, await policies.RuntimeAsync(agentId, ct),
            assigned.Select(x => x.Id).ToArray());
        var interval = (await operational.GetAsync(ct)).Policy.StatusIntervalSeconds;
        await transaction.CommitAsync(ct);
        return new(configuration, deployments, interval);
    }
}

// Session-local fingerprints intentionally start empty on EVERY connection, including CP restarts.
public sealed class AgentConfigurationTransfer
{
    private string? fingerprint;
    private Dictionary<Guid, string> known = [];
    public async Task<bool> SendAsync(AgentConfigurationSnapshot snapshot,
        Func<AgentWireMessage, CancellationToken, Task> send, Action<string> expecting, CancellationToken ct)
    {
        static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web)));
        var hashes = snapshot.Deployments.ToDictionary(x => x.Id, Hash);
        var current = Hash(new { snapshot.Configuration, Deployments = snapshot.Deployments.Select(x => new { x.Id, Hash = hashes[x.Id] }).ToArray() });
        if (current == fingerprint) return false;
        expecting(current);
        await send(new("configuration.begin", Guid.Empty, Configuration: snapshot.Configuration), ct);
        foreach (var deployment in snapshot.Deployments)
            if (!known.TryGetValue(deployment.Id, out var previous) || previous != hashes[deployment.Id])
                await send(new("configuration.deployment", deployment.Id, Deployment: deployment), ct);
        await send(new("configuration.complete", Guid.Empty, Data: current), ct);
        // A failed send never advances these fingerprints; reconnect also always sends everything.
        fingerprint = current;
        known = hashes;
        return true;
    }
}
