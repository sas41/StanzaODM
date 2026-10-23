using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Core.Deployments;

namespace Stanza.ControlPlane.Sources;

public sealed class SourceService(StanzaDbContext db, AccessService access, IDataProtectionProvider protection,
    IAgentCommandDispatcher dispatcher, Configuration.AgentPolicyService? policies = null) : ISourceSnapshotProvider
{
    public async Task<IReadOnlyList<SourceAgentSummary>> ListAgentsAsync(Guid actorId, CancellationToken ct = default)
    {
        var rows = await db.Set<AgentEntity>().AsNoTracking().Where(x => x.Status == "approved").ToListAsync(ct);
        var result = new List<SourceAgentSummary>();
        foreach (var row in rows)
            if (await Allowed(actorId, "agent.read", "agent", row.Id, ct)) result.Add(new(row.Id, row.Hostname));
        return result;
    }

    public async Task<IReadOnlyList<DeploymentSourceSummary>> ListDeploymentsAsync(Guid actorId, CancellationToken ct = default)
    {
        var ids = await db.Set<DeploymentDraftEntity>().AsNoTracking().OrderBy(x => x.Name).Select(x => x.Id).ToArrayAsync(ct);
        var result = new List<DeploymentSourceSummary>();
        foreach (var id in ids)
            if (await Allowed(actorId, "deployment.read", "deployment", id, ct)) result.Add(await ReadAsync(actorId, id, ct));
        return result;
    }

    public Task<bool> CanWriteCredentialAsync(Guid actorId, Guid? id, CancellationToken ct = default) =>
        Allowed(actorId, "credential.write", "credential", id, ct);

    public async Task<IReadOnlyList<CredentialSummary>> ListCredentialsAsync(Guid actorId, CancellationToken ct = default)
    {
        var rows = await db.Set<SourceCredentialEntity>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var result = new List<CredentialSummary>();
        foreach (var row in rows)
            if (await Allowed(actorId, "credential.read", "credential", row.Id, ct)) result.Add(Summary(row));
        return result;
    }

    public async Task<IReadOnlyList<CredentialSummary>> UsableCredentialsAsync(Guid actorId, CancellationToken ct = default)
    {
        var rows = await db.Set<SourceCredentialEntity>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var result = new List<CredentialSummary>();
        foreach (var row in rows)
            if (await Allowed(actorId, "credential.use", "credential", row.Id, ct)) result.Add(Summary(row));
        return result;
    }

    public async Task<CredentialSummary> ReadCredentialAsync(Guid actorId, Guid id, CancellationToken ct = default)
    {
        await Require(actorId, "credential.read", "credential", id, ct);
        return Summary(await Credential(id, ct));
    }

    public async Task<CredentialSecret> RevealAsync(Guid actorId, Guid id, CancellationToken ct = default) =>
        await Serialized(async () =>
        {
            await Require(actorId, "credential.reveal", "credential", id, ct);
            return Secret(await Credential(id, ct));
        }, ct);

    public Task<CredentialSummary> SaveCredentialAsync(Guid actorId, Guid? id, long expectedVersion,
        CredentialInput input, CancellationToken ct = default) => Serialized(async () =>
    {
        await Require(actorId, "credential.write", "credential", id, ct);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || input.Name.Any(char.IsControl))
            throw new SourceException("Credential name must contain 1–100 printable characters.");
        if (input.Kind is not ("git" or "registry")) throw new SourceException("Select Git or registry credentials.");
        var endpoint = SourceValidation.Url(input.Endpoint, input.Kind == "registry");
        if (string.IsNullOrEmpty(input.Username) || input.Username.Length > 512 || input.Username.Any(char.IsControl) ||
            string.IsNullOrEmpty(input.Password) || input.Password.Length > 16384 || input.Password.Any(char.IsControl))
            throw new SourceException("Supply a username and password/token without control characters (maximum 512/16384 characters).");
        var row = id.HasValue ? await Credential(id.Value, ct) : new SourceCredentialEntity { Id = Guid.NewGuid() };
        if (id.HasValue && row.Version != expectedVersion) throw Conflict();
        if (id.HasValue && (row.Kind != input.Kind || row.Endpoint != endpoint) &&
            (await db.Set<DeploymentGitSourceEntity>().AnyAsync(x => x.CredentialId == id, ct) ||
             await db.Set<DeploymentRegistryBindingEntity>().AnyAsync(x => x.CredentialId == id, ct)))
            throw new SourceException("Unbind this credential before changing its type or endpoint.");
        row.Name = input.Name;
        row.Kind = input.Kind;
        row.Endpoint = endpoint;
        row.Version++;
        row.ProtectedSecret = Protector(row.Id).Protect(JsonSerializer.Serialize(new CredentialSecret(input.Username, input.Password)));
        if (id.HasValue)
        {
            db.Update(row);
            db.Entry(row).Property(x => x.Version).OriginalValue = expectedVersion;
        }
        else db.Add(row);
        await db.SaveChangesAsync(ct);
        return Summary(row);
    }, ct);

    public Task DeleteCredentialAsync(Guid actorId, Guid id, long expectedVersion, CancellationToken ct = default) =>
        Serialized(async () =>
        {
            await Require(actorId, "credential.write", "credential", id, ct);
            var row = await Credential(id, ct);
            if (row.Version != expectedVersion) throw Conflict();
            if (await db.Set<DeploymentGitSourceEntity>().AnyAsync(x => x.CredentialId == id, ct) ||
                await db.Set<DeploymentRegistryBindingEntity>().AnyAsync(x => x.CredentialId == id, ct))
                throw new SourceException("This credential is still referenced. Remove its Git/registry bindings before deleting it.");
            db.Remove(row);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    public async Task<DeploymentSourceSummary> ReadAsync(Guid actorId, Guid deploymentId, CancellationToken ct = default)
    {
        await Require(actorId, "deployment.read", "deployment", deploymentId, ct);
        var draft = await Draft(deploymentId, ct);
        return new(deploymentId, draft.Name, draft.TargetAgentId, (await GitRows(deploymentId, ct)).Select(Summary).ToArray(),
            await db.Set<DeploymentRegistryBindingEntity>().AsNoTracking().Where(x => x.DeploymentId == deploymentId).Select(x => x.CredentialId).ToArrayAsync(ct),
            await Allowed(actorId, "sources.manage", "deployment", deploymentId, ct) &&
            await Allowed(actorId, "files.write", "deployment", deploymentId, ct));
    }

    public Task<GitSourceSummary> SaveGitAsync(Guid actorId, Guid deploymentId, Guid? id, long expectedVersion,
        GitSourceInput input, CancellationToken ct = default) => Serialized(async () =>
    {
        await Manage(actorId, deploymentId, ct);
        var draft = await Draft(deploymentId, ct);
        var url = SourceValidation.Url(input.Url);
        var reference = SourceValidation.Reference(input.Reference);
        var destination = SourceValidation.Destination(input.Destination);
        if (input.CredentialId.HasValue) await GitCredential(actorId, input.CredentialId.Value, url, ct);
        var rows = await GitRows(deploymentId, ct);
        if (!id.HasValue && rows.Count >= 32) throw new SourceException("A deployment supports at most 32 Git sources.");
        var row = id.HasValue ? rows.SingleOrDefault(x => x.Id == id) ?? throw new SourceException("That source is unavailable.")
            : new DeploymentGitSourceEntity { Id = Guid.NewGuid(), DeploymentId = deploymentId, Position = rows.Count };
        if (id.HasValue && row.Version != expectedVersion) throw Conflict();
        await ValidateDestinations(draft, rows.Where(x => x.Id != row.Id).Select(x => x.Destination).Append(destination.Value), ct);
        row.Url = url; row.Reference = reference; row.Destination = destination.Value; row.CredentialId = input.CredentialId; row.Version++;
        if (id.HasValue)
        {
            db.Update(row);
            db.Entry(row).Property(x => x.Version).OriginalValue = expectedVersion;
        }
        else db.Add(row);
        await db.SaveChangesAsync(ct);
        return Summary(row);
    }, ct);

    public Task DeleteGitAsync(Guid actorId, Guid deploymentId, Guid id, long expectedVersion, CancellationToken ct = default) =>
        Serialized(async () =>
        {
            await Manage(actorId, deploymentId, ct);
            var row = (await GitRows(deploymentId, ct)).SingleOrDefault(x => x.Id == id) ?? throw new SourceException("That source is unavailable.");
            if (row.Version != expectedVersion) throw Conflict();
            db.Remove(row); await db.SaveChangesAsync(ct); return true;
        }, ct);

    public Task ReorderAsync(Guid actorId, Guid deploymentId, IReadOnlyList<Guid> ids, IReadOnlyList<long> versions,
        CancellationToken ct = default) => Serialized(async () =>
    {
        await Manage(actorId, deploymentId, ct);
        var rows = await GitRows(deploymentId, ct);
        if (ids.Count != rows.Count || ids.Distinct().Count() != rows.Count || versions.Count != rows.Count ||
            rows.Any(x => !ids.Contains(x.Id))) throw new SourceException("Supply every source exactly once to reorder.");
        for (var i = 0; i < ids.Count; i++)
        {
            var row = rows.Single(x => x.Id == ids[i]);
            if (row.Version != versions[i]) throw Conflict();
            row.Position = i; row.Version++; db.Update(row);
            db.Entry(row).Property(x => x.Version).OriginalValue = versions[i];
        }
        await db.SaveChangesAsync(ct); return true;
    }, ct);

    public Task SetRegistriesAsync(Guid actorId, Guid deploymentId, IReadOnlyList<Guid> credentialIds, IReadOnlyList<Guid> expectedCredentialIds,
        CancellationToken ct = default) => Serialized(async () =>
    {
        await Manage(actorId, deploymentId, ct);
        await Draft(deploymentId, ct);
        var currentIds = await db.Set<DeploymentRegistryBindingEntity>().AsNoTracking().Where(x => x.DeploymentId == deploymentId).Select(x => x.CredentialId).ToArrayAsync(ct);
        if (!currentIds.Order().SequenceEqual(expectedCredentialIds.Order())) throw Conflict();
        if (credentialIds.Count > 32 || credentialIds.Distinct().Count() != credentialIds.Count)
            throw new SourceException("Select at most 32 distinct registry credentials.");
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in credentialIds)
        {
            await Require(actorId, "credential.use", "credential", id, ct);
            var row = await Credential(id, ct);
            if (row.Kind != "registry") throw new SourceException("Only registry credentials can be bound as registries.");
            if (!endpoints.Add(row.Endpoint)) throw new SourceException("Select only one credential for each registry endpoint.");
        }
        await db.Set<DeploymentRegistryBindingEntity>().Where(x => x.DeploymentId == deploymentId).ExecuteDeleteAsync(ct);
        foreach (var id in credentialIds) db.Add(new DeploymentRegistryBindingEntity { DeploymentId = deploymentId, CredentialId = id });
        await db.SaveChangesAsync(ct); return true;
    }, ct);

    public Task<DeploymentSourcesSnapshot> PrepareAsync(Guid actorId, Guid deploymentId, CancellationToken token = default) =>
        Serialized(async () =>
        {
            await Require(actorId, "deployment.read", "deployment", deploymentId, token);
            // The operation executor checks the concrete lifecycle permission; source use does not require source-edit or reveal rights.
            var draft = await Draft(deploymentId, token);
            var rows = await GitRows(deploymentId, token);
            await ValidateDestinations(draft, rows.Select(x => x.Destination), token);
            var git = new List<AgentGitSource>();
            foreach (var row in rows)
            {
                SourceValidation.Url(row.Url); SourceValidation.Reference(row.Reference);
                CredentialSecret? secret = row.CredentialId.HasValue ? Secret(await GitCredential(actorId, row.CredentialId.Value, row.Url, token)) : null;
                git.Add(new(row.Url, row.Reference, row.Destination, secret?.Username, secret?.Password));
            }
            var registries = new List<AgentRegistryCredential>();
            var ids = await db.Set<DeploymentRegistryBindingEntity>().AsNoTracking().Where(x => x.DeploymentId == deploymentId).Select(x => x.CredentialId).ToArrayAsync(token);
            foreach (var id in ids)
            {
                await Require(actorId, "credential.use", "credential", id, token);
                var row = await Credential(id, token);
                if (row.Kind != "registry") throw new SourceException("A registry binding is invalid.");
                SourceValidation.Url(row.Endpoint, true);
                var secret = Secret(row);
                registries.Add(new(row.Endpoint, secret.Username, secret.Password));
            }
            return new DeploymentSourcesSnapshot(git, registries);
        }, token);

    public async Task<BranchPage> BranchesAsync(Guid actorId, Guid deploymentId, Guid sourceId,
        string? search = null, int page = 0, CancellationToken ct = default)
    {
        var query = (search ?? "").Trim();
        if (query.Length > 256 || page < 0 || page > 199) throw new SourceException("Invalid reference search or page.");
        // Hold the security lock through dispatch so revocation cannot race a new secret-bearing command.
        var result = await Serialized(async () =>
        {
            await Require(actorId, "deployment.read", "deployment", deploymentId, ct);
            var draft = await Draft(deploymentId, ct);
            if (!draft.TargetAgentId.HasValue) throw new SourceException("Select a deployment Agent before browsing remote references.");
            await Agent(actorId, draft.TargetAgentId.Value, ct);
            var row = (await GitRows(deploymentId, ct)).SingleOrDefault(x => x.Id == sourceId) ?? throw new SourceException("That source is unavailable.");
            SourceValidation.Url(row.Url); SourceValidation.Reference(row.Reference);
            var secret = row.CredentialId.HasValue ? Secret(await GitCredential(actorId, row.CredentialId.Value, row.Url, ct)) : null;
            var response = await Dispatch(draft.TargetAgentId.Value, new(Guid.NewGuid(), deploymentId, "git-branches", [], [],
                Sources: [new(row.Url, row.Reference, row.Destination, secret?.Username, secret?.Password)]), ct);
            return (row.Reference, References(response));
        }, ct);
        var filtered = result.Item2.Where(x => x.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(filtered.Skip(page * 50).Take(50).ToArray(), result.Reference,
            !IsSelectedPresent(result.Item2, result.Reference), page, filtered.Length > (page + 1) * 50, query);
    }

    public Task TestCredentialAsync(Guid actorId, Guid id, Guid targetAgentId, CancellationToken ct = default) =>
        Serialized(async () =>
        {
            await Require(actorId, "credential.use", "credential", id, ct);
            await Agent(actorId, targetAgentId, ct);
            var row = await Credential(id, ct);
            var secret = Secret(row);
            var request = row.Kind == "registry"
                ? new AgentWorkRequest(Guid.NewGuid(), Guid.Empty, "registry-check", [], [], Registries: [new(row.Endpoint, secret.Username, secret.Password)])
                : new AgentWorkRequest(Guid.NewGuid(), Guid.Empty, "git-branches", [], [], Sources: [new(row.Endpoint, "HEAD", "checkout", secret.Username, secret.Password)]);
            await Dispatch(targetAgentId, request, ct);
            return true;
        }, ct);

    private async Task<AgentWorkResponse> Dispatch(Guid agentId, AgentWorkRequest request, CancellationToken ct)
    {
        try
        {
            if (policies is not null) request = request with { Policy = await policies.RuntimeAsync(agentId, ct) };
            var response = await dispatcher.DispatchAsync(agentId, request, ct);
            if (!response.Succeeded) throw new SourceException("The Agent could not complete the connection check. Check its connectivity, endpoint, and credentials.");
            return response;
        }
        catch (AgentUnavailableException) { throw new SourceException("The selected Agent is offline. Reconnect it and refresh."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SourceException("The Agent connection check timed out. Retry or check the endpoint."); }
    }

    private static string[] References(AgentWorkResponse response)
    {
        if (response.Output is null || response.Output.Length > 1024 * 1024) throw new SourceException("The Agent returned an invalid or oversized reference listing.");
        try
        {
            var refs = JsonSerializer.Deserialize<string[]>(response.Output) ?? [];
            if (refs.Length > 10000) throw new SourceException("The remote has too many references; use an explicit branch/tag/commit.");
            foreach (var reference in refs) SourceValidation.Reference(reference);
            return refs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { throw new SourceException("The Agent returned an invalid reference listing."); }
    }
    private static bool IsSelectedPresent(string[] refs, string current) =>
        refs.Contains(current, StringComparer.Ordinal) || refs.Contains("refs/heads/" + current, StringComparer.Ordinal) ||
        refs.Contains("refs/tags/" + current, StringComparer.Ordinal);

    private async Task Agent(Guid actorId, Guid id, CancellationToken ct)
    {
        await Require(actorId, "agent.read", "agent", id, ct);
        if (!await db.Set<AgentEntity>().AsNoTracking().AnyAsync(x => x.Id == id && x.Status == "approved", ct))
            throw new SourceException("Select a registered, approved Agent. Docker/Agent access is privileged and can reach its internal network.");
    }

    private async Task<SourceCredentialEntity> GitCredential(Guid actorId, Guid id, string url, CancellationToken ct)
    {
        await Require(actorId, "credential.use", "credential", id, ct);
        var row = await Credential(id, ct);
        // Bind to the exact repository, not merely its host; prevents reusing a scoped token on a different repository.
        if (row.Kind != "git" || row.Endpoint != url) throw new SourceException("Select a Git credential configured for this exact repository URL.");
        return row;
    }

    private async Task ValidateDestinations(DeploymentDraftEntity draft, IEnumerable<string> values, CancellationToken ct)
    {
        var paths = values.Select(SourceValidation.Destination).ToArray();
        for (var i = 0; i < paths.Length; i++)
            for (var j = i + 1; j < paths.Length; j++)
                if (Collides(paths[i], paths[j])) throw new SourceException("Git checkout destinations must not overlap.");
        if (paths.Length == 0) return;
        var revision = await db.Set<DeploymentDraftRevisionEntity>().AsNoTracking().SingleAsync(x => x.DeploymentId == draft.Id && x.Number == draft.Revision, ct);
        try
        {
            using var payload = JsonDocument.Parse(protection.CreateProtector("Stanza.DeploymentDraft.v1", draft.Id.ToString("N"),
                draft.Revision.ToString(CultureInfo.InvariantCulture)).Unprotect(revision.ProtectedContent));
            var files = new List<string> { DeploymentDraftService.ComposePath, DeploymentDraftService.EnvironmentPath };
            foreach (var file in payload.RootElement.GetProperty("Files").EnumerateArray()) files.Add(file.GetProperty("Path").GetString()!);
            if (files.Select(DeploymentPath.Parse).Any(file => paths.Any(path => Collides(path, file))))
                throw new SourceException("A Git checkout destination overlaps a managed deployment file. Choose an isolated directory.");
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        { throw new SourceException("The encrypted deployment configuration could not be read. Check persisted encryption keys."); }
    }
    private static bool Collides(DeploymentPath a, DeploymentPath b) => a.IsSameOrAncestorOf(b) || b.IsSameOrAncestorOf(a);
    private IDataProtector Protector(Guid id) => protection.CreateProtector("Stanza.SourceCredential.v1", id.ToString("N"));
    private CredentialSecret Secret(SourceCredentialEntity row)
    {
        try { return JsonSerializer.Deserialize<CredentialSecret>(Protector(row.Id).Unprotect(row.ProtectedSecret)) ?? throw new JsonException(); }
        catch (Exception error) when (error is CryptographicException or JsonException)
        { throw new SourceException("The credential could not be decrypted. Check persisted encryption keys or replace the credential."); }
    }
    private async Task<SourceCredentialEntity> Credential(Guid id, CancellationToken ct) =>
        await db.Set<SourceCredentialEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new SourceException("That credential is unavailable.");
    private async Task<DeploymentDraftEntity> Draft(Guid id, CancellationToken ct) =>
        await db.Set<DeploymentDraftEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new SourceException("That deployment is unavailable.");
    private Task<List<DeploymentGitSourceEntity>> GitRows(Guid id, CancellationToken ct) => db.Set<DeploymentGitSourceEntity>()
        .AsNoTracking().Where(x => x.DeploymentId == id).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync(ct);
    private Task<bool> Allowed(Guid actorId, string action, string type, Guid? id, CancellationToken ct) => access.IsAllowedAsync(actorId, action, type, id, ct);
    private async Task Require(Guid actorId, string action, string type, Guid? id, CancellationToken ct)
    { if (!await Allowed(actorId, action, type, id, ct)) throw new SourceAccessException(); }
    private async Task Manage(Guid actorId, Guid id, CancellationToken ct)
    {
        await Require(actorId, "deployment.read", "deployment", id, ct);
        await Require(actorId, "sources.manage", "deployment", id, ct);
        await Require(actorId, "files.write", "deployment", id, ct);
    }
    private async Task<T> Serialized<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        // Operation snapshots and atomic imports already own a transaction. Never start a nested EF transaction,
        // commit the caller's work, or leave module writes behind after a failure.
        var existing = db.Database.CurrentTransaction;
        await using var owned = existing is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var transaction = owned ?? existing!;
        var savepoint = owned is null ? "sources_" + Guid.NewGuid().ToString("N") : null;
        if (savepoint is not null) await transaction.CreateSavepointAsync(savepoint, ct);
        var succeeded = false;
        try
        {
            await SecurityWriteGuard.LockAsync(db, ct);
            var result = await operation();
            if (owned is not null) await owned.CommitAsync(ct);
            else await transaction.ReleaseSavepointAsync(savepoint!, ct);
            succeeded = true;
            return result;
        }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        finally
        {
            if (!succeeded && savepoint is not null)
            {
                await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                await transaction.ReleaseSavepointAsync(savepoint, CancellationToken.None);
            }
            // Detach only this module's entities; never disturb another service's pending work.
            foreach (var entry in db.ChangeTracker.Entries().Where(x => x.Entity is SourceCredentialEntity or DeploymentGitSourceEntity or DeploymentRegistryBindingEntity).ToArray())
                entry.State = EntityState.Detached;
        }
    }
    private static SourceException Conflict() => new("This configuration changed in another session. Reload before saving.");
    private static CredentialSummary Summary(SourceCredentialEntity x) => new(x.Id, x.Name, x.Kind, x.Endpoint, x.Version);
    private static GitSourceSummary Summary(DeploymentGitSourceEntity x) => new(x.Id, x.DeploymentId, x.Url, x.Reference, x.Destination, x.CredentialId, x.Position, x.Version);
}
