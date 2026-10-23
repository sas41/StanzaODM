using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.ControlPlane.Audit;

namespace Stanza.ControlPlane.Portability;

public sealed class PortabilityService(StanzaDbContext db, AccessService access, DeploymentDraftService drafts,
    IPortableDraftStore store, IDataProtectionProvider protection, TimeProvider clock,
    IEnumerable<IPortableConfigurationParticipant> participants, AuditService? audit = null)
{
    public IReadOnlyList<string> SupportedSections => participants.Select(x => x.Kind).Order().ToArray();

    public async Task<byte[]> ExportAsync(Guid actor, PortableExportSelection selection, CancellationToken ct = default)
    {
        // Acknowledgement is checked BEFORE reading or generating any secret material.
        if (!selection.PlaintextAcknowledged) throw new PortabilityException("Acknowledge that credentials, tokens, environment values and files will be readable plaintext before generating an export.");
        if (selection.Deployments.Count == 0 && selection.Sections.Count == 0) throw new PortabilityException("Select at least one deployment or supported section to export.");
        if (selection.Deployments.Count > 100 || selection.Deployments.Distinct().Count() != selection.Deployments.Count)
            throw new PortabilityException("Select at most 100 distinct deployments.");
        var selectedParticipants = ResolveParticipants(selection.Sections);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        if (selection.Sections.Count > 0 && (selection.Deployments.Count == 0 || selectedParticipants.Any(p => p.Kind is not ("sources" or "automation"))))
            await RequireAsync(actor, "configuration.export", "instance", null, ct);
        var agents = new List<PortableAgent>();
        var agentIds = new Dictionary<Guid, string>();
        var deployments = new List<PortableDeployment>();
        foreach (var id in selection.Deployments)
        {
            if (!await access.IsAllowedAsync(actor, "configuration.export", "deployment", id, ct)
                && !await access.IsAllowedAsync(actor, "configuration.export", "instance", null, ct)) throw new PortabilityAccessException();
            foreach (var action in new[] { "deployment.read", "files.read", "environment.reveal" })
                await RequireAsync(actor, action, "deployment", id, ct);
            var draft = await drafts.ReadAsync(actor, id, ct: ct);
            string? agentRef = null;
            if (draft.Draft.TargetAgentId is { } agent)
            {
                await RequireAsync(actor, "agent.read", "agent", agent, ct);
                var agentRow = await db.Set<AgentEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == agent, ct)
                    ?? throw new PortabilityException("The selected deployment references an unavailable Agent; resolve placement before export.");
                agentIds.TryGetValue(agent, out agentRef);
                // Logical IDs and labels deliberately contain no Agent identity or signing key.
                agentRef ??= "agent-" + agents.Count;
                if (!agents.Any(x => x.Id == agentRef)) { agents.Add(new(agentRef, agentRow.Hostname)); agentIds[agent] = agentRef; }
            }
            deployments.Add(await store.ReadAsync(actor, id, "deployment-" + deployments.Count, agentRef, ct));
        }
        var sections = new List<PortableSection>();
        var resourceReferences = new Dictionary<Guid, PortableResourceReference>();
        var exportContext = new PortableParticipantContext(actor, selection.Deployments,
            selection.Deployments.Select((id, index) => (id, logical: "deployment-" + index)).ToDictionary(x => x.id, x => x.logical), agentIds, resourceReferences);
        await CollectReferencesAsync(exportContext, selectedParticipants, agents, resourceReferences, ct);
        foreach (var participant in selectedParticipants)
        {
            await RequireSecurityPolicyAsync(actor, participant, ct);
            sections.Add(new(participant.Kind, await participant.ExportAsync(exportContext, ct)));
        }
        var bundle = WithSourceReservations(new PortableBundle(1, deployments, agents, sections) { References = resourceReferences.Values.ToArray() });
        PortableBundleValidator.Validate(bundle);
        var output = JsonSerializer.SerializeToUtf8Bytes(bundle, PortableBundleValidator.JsonOptions);
        if (audit is not null)
        {
            foreach (var id in selection.Deployments) await audit.WriteAsync(actor, "configuration.export.deployment", id.ToString(), "plaintext-acknowledged", ct);
            foreach (var reference in resourceReferences.Where(r => r.Value.Type == "credential"))
                await audit.WriteAsync(actor, "configuration.export.credential", reference.Key.ToString(), "plaintext-acknowledged", ct);
            await audit.WriteAsync(actor, "configuration.export", "instance", $"deployments:{deployments.Count};sections:{sections.Count}", ct);
        }
        await transaction.CommitAsync(ct);
        return output;
    }

    public async Task<PortablePreview> PreviewAsync(Guid actor, byte[] bytes, PortableImportMapping mapping, CancellationToken ct = default)
    {
        await RequireAsync(actor, "configuration.import", "instance", null, ct);
        var bundle = WithSourceReservations(PortableBundleValidator.Parse(bytes));
        if (bundle.Deployments.Count > 0) await RequireAsync(actor, "deployment.create", "instance", null, ct);
        var content = new PreviewContent(bundle, mapping);
        var conflicts = await ValidateImportAsync(actor, content, new Dictionary<string, Guid>(), ct);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(content, PortableBundleValidator.JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(canonical));
        var existing = await db.Set<PortableImportEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor && x.Fingerprint == hash, ct);
        if (existing is not null) throw new PortabilityException("This exact preview already exists or has been applied; use its existing run instead of submitting duplicates.");
        var row = new PortableImportEntity { Id = Guid.NewGuid(), ActorId = actor, Fingerprint = hash,
            ExpiresAt = clock.GetUtcNow().AddMinutes(20).ToUnixTimeSeconds() };
        row.ProtectedContent = Protector(row.Id, actor).Protect(canonical);
        db.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new PortabilityException("A duplicate preview was created concurrently; reload existing runs."); }
        finally { db.Entry(row).State = EntityState.Detached; }
        return Preview(row, content, conflicts);
    }

    public async Task<PortablePreview> ReadPreviewAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        await RequireAsync(actor, "configuration.import", "instance", null, ct);
        var row = await RequiredRunAsync(actor, id, ct);
        if (row.State != "preview" || row.ExpiresAt <= clock.GetUtcNow().ToUnixTimeSeconds()) throw new PortabilityException("This preview expired or was already claimed; inspect its result instead.");
        var content = ReadContent(row);
        return Preview(row, content, await ValidateImportAsync(actor, content, new Dictionary<string, Guid>(), ct));
    }

    public async Task<PortableApplyResult> ApplyAsync(Guid actor, Guid id, string fingerprint, bool acknowledged, CancellationToken ct = default)
    {
        if (!acknowledged) throw new PortabilityException("Confirm the preview, inactive automation, and no persistent data transfer before applying.");
        await RequireAsync(actor, "configuration.import", "instance", null, ct);
        var row = await RequiredRunAsync(actor, id, ct);
        if (row.State != "preview" || row.ExpiresAt <= clock.GetUtcNow().ToUnixTimeSeconds() || !string.Equals(row.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new PortabilityException("The preview has expired, changed, or was already applied. No repeat application is allowed.");
        var content = ReadContent(row);
        if (content.Bundle.Deployments.Count > 0) await RequireAsync(actor, "deployment.create", "instance", null, ct);
        var conflicts = await ValidateImportAsync(actor, content, new Dictionary<string, Guid>(), ct);
        if (conflicts.Count > 0) throw new PortabilityException("Resolve all preview conflicts before applying.");
        var claimed = await db.Set<PortableImportEntity>().Where(x => x.Id == id && x.ActorId == actor && x.State == "preview" && x.ExpiresAt > clock.GetUtcNow().ToUnixTimeSeconds())
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "applying"), ct);
        if (claimed != 1) throw new PortabilityException("Another request already claimed this import.");
        var created = new Dictionary<string, Guid>();
        var resources = new Dictionary<string, Guid>();
        var completedSections = new List<string>();
        string? currentSection = "deployment-workspaces";
        try
        {
            if (audit is not null) await audit.WriteAsync(actor, "configuration.import.claimed", id.ToString(), "staged-inactive", ct);
            await SaveResourceProgressAsync(id, resources, completedSections, currentSection, ct);
            foreach (var deployment in content.Bundle.Deployments)
            {
                try
                {
                    created[deployment.Id] = await store.CreateAsync(actor, deployment, Name(deployment, content.Mapping),
                        deployment.Agent is null ? null : content.Mapping.Agents[deployment.Agent], ct);
                }
                catch (PortablePartialCreationException partial)
                {
                    created[deployment.Id] = partial.DeploymentId;
                    throw;
                }
                await SaveProgressAsync(id, created, ct);
            }
            var context = ImportContext(actor, content, created, resources);
            foreach (var section in content.Bundle.Sections.OrderBy(s => ResolveParticipants([s.Kind]).Single().Priority))
            {
                currentSection = section.Kind;
                await SaveResourceProgressAsync(id, resources, completedSections, currentSection, ct);
                var participant = ResolveParticipants([section.Kind]).Single();
                await RequireAsync(actor, "configuration.import", "instance", null, ct);
                await RequireSecurityPolicyAsync(actor, participant, ct);
                var errors = await participant.ValidateAsync(section.Content, context, ct);
                if (errors.Count != 0) throw new PortabilityException("A participant's references changed during import.");
                await participant.ApplyAsync(section.Content, context, ct);
                completedSections.Add(section.Kind);
                await SaveResourceProgressAsync(id, resources, completedSections, currentSection, ct);
            }
            await SaveProgressAsync(id, created, CancellationToken.None);
            await FinishAsync(id, "complete", null, CancellationToken.None);
            await SaveResourceProgressAsync(id, resources, completedSections, null, CancellationToken.None);
            if (audit is not null) await audit.WriteAsync(actor, "configuration.import.complete", id.ToString(), "inactive", CancellationToken.None);
            return new(id, "complete", created.Values.ToArray(), null) { CreatedResources = resources, CompletedSections = completedSections };
        }
        catch (Exception)
        {
            // Each existing service may commit its own transaction. Preserve IDs and explicitly report partial application.
            await SaveProgressAsync(id, created, CancellationToken.None);
            await SaveResourceProgressAsync(id, resources, completedSections, currentSection, CancellationToken.None);
            const string message = "Import interrupted. Configuration may be partially applied; inspect listed deployments and participant resources. Workloads were not started. This run cannot be replayed.";
            await FinishAsync(id, "partial", message, CancellationToken.None);
            if (audit is not null) await audit.WriteAsync(actor, "configuration.import.partial", id.ToString(), "inspect-staged-results", CancellationToken.None);
            return new(id, "partial", created.Values.ToArray(), message) { CreatedResources = resources, CompletedSections = completedSections, CurrentSection = currentSection };
        }
    }

    public async Task<PortableApplyResult> ReadResultAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        await RequireAsync(actor, "configuration.import", "instance", null, ct);
        var row = await RequiredRunAsync(actor, id, ct);
        return new(id, row.State, JsonSerializer.Deserialize<Guid[]>(row.CreatedDeployments) ?? [],
            row.State == "applying" ? "Import is applying or was interrupted; inspect configuration before taking any further action. It will not automatically replay." : row.Failure)
        { CreatedResources = JsonSerializer.Deserialize<Dictionary<string, Guid>>(row.CreatedResources) ?? [],
            CompletedSections = JsonSerializer.Deserialize<string[]>(row.CompletedSections) ?? [], CurrentSection = row.CurrentSection };
    }

    public async Task<Guid> CloneAsync(Guid actor, Guid sourceId, string name, Guid destinationAgent, bool acknowledged,
        IReadOnlyList<string>? selectedSections = null, CancellationToken ct = default)
    {
        if (!acknowledged) throw new PortabilityException("Acknowledge destination conflicts, different inherited environment, and that persistent data is not transferred.");
        await RequireAsync(actor, "deployment.clone", "deployment", sourceId, ct);
        await RequireAsync(actor, "configuration.import", "instance", null, ct);
        await RequireAsync(actor, "deployment.create", "instance", null, ct);
        await ValidateAgentAsync(actor, destinationAgent, ct);
        var source = await store.ReadAsync(actor, sourceId, "source", "destination", ct);
        var names = new Dictionary<string, string> { ["source"] = name };
        var mapping = new PortableImportMapping(new Dictionary<string, Guid> { ["destination"] = destinationAgent }, names, new Dictionary<string, string>());
        var selected = ResolveParticipants(selectedSections ?? []);
        if (selected.Any(p => p.Kind is not ("sources" or "automation")))
            throw new PortabilityException("Deployment cloning copies source/credential and automation definitions only. Instance settings, inherited environment, Agent templates and security grants require an explicit import, not a clone side effect.");
        var sections = new List<PortableSection>();
        var referenceMap = new Dictionary<Guid, PortableResourceReference>();
        var agentMap = new Dictionary<Guid, string> { [destinationAgent] = "destination" };
        var cloneAgents = new List<PortableAgent> { new("destination", "Destination") };
        var exportContext = new PortableParticipantContext(actor, [sourceId], new Dictionary<Guid, string> { [sourceId] = "source" }, agentMap, referenceMap);
        await CollectReferencesAsync(exportContext, selected, cloneAgents, referenceMap, ct);
        foreach (var participant in selected)
        {
            if (participant.SecurityPolicy) throw new PortabilityException("Clone does not copy security policy or grants; assign access explicitly using account management.");
            var section = new PortableSection(participant.Kind, await participant.ExportAsync(exportContext, ct));
            sections.Add(section);
        }
        // Clone retains explicitly selected external references, but never copies security grants implicitly.
        var resourceMappings = referenceMap.ToDictionary(x => x.Value.Id, x => x.Key.ToString());
        mapping = mapping with { Resources = resourceMappings, Agents = agentMap.ToDictionary(x => x.Value, x => x.Key) };
        var bundle = WithSourceReservations(new PortableBundle(1, [source], cloneAgents, sections) { References = referenceMap.Values.ToArray() });
        PortableBundleValidator.Validate(bundle);
        var cloneContent = new PreviewContent(bundle, mapping);
        var conflicts = await ValidateImportAsync(actor, cloneContent, new Dictionary<string, Guid>(), ct);
        if (conflicts.Count > 0) throw new PortabilityException("Clone references or configuration conflict; use import preview for explicit mappings.");
        if (await db.Set<DeploymentDraftEntity>().AnyAsync(x => x.Name == name, ct)) throw new PortabilityException("That deployment name is already in use.");
        var id = await store.CreateAsync(actor, bundle.Deployments[0], name, destinationAgent, ct);
        try
        {
            var importContext = ImportContext(actor, cloneContent, new Dictionary<string, Guid> { ["source"] = id }, new Dictionary<string, Guid>());
            foreach (var section in sections.OrderBy(s => selected.Single(x => x.Kind == s.Kind).Priority))
                await selected.Single(x => x.Kind == section.Kind).ApplyAsync(section.Content,
                    importContext, ct);
            if (audit is not null) await audit.WriteAsync(actor, "configuration.clone.created", id.ToString(), "inactive-no-data-transfer", ct);
        }
        catch (Exception) { throw new PortablePartialCreationException(id); }
        return id;
    }

    internal async Task ValidateAgentAsync(Guid actor, Guid id, CancellationToken ct)
    {
        await RequireAsync(actor, "agent.read", "agent", id, ct);
        await RequireAsync(actor, "agent.manage", "agent", id, ct);
        if (!await db.Set<AgentEntity>().AnyAsync(x => x.Id == id && x.Status == "approved", ct))
            throw new PortabilityException("Map each Agent reference to a registered, approved Agent.");
    }

    internal async Task RequireAsync(Guid actor, string action, string type, Guid? id, CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(actor, action, type, id, ct)) throw new PortabilityAccessException();
    }

    private async Task<List<string>> ValidateImportAsync(Guid actor, PreviewContent content, IReadOnlyDictionary<string, Guid> created, CancellationToken ct)
    {
        PortableBundleValidator.Validate(content.Bundle);
        if (content.Mapping is null || content.Mapping.Agents is null || content.Mapping.DeploymentNames is null || content.Mapping.Resources is null
            || content.Mapping.Agents.Count > 100 || content.Mapping.DeploymentNames.Count > 100 || content.Mapping.Resources.Count > 500
            || content.Mapping.DeploymentNames.Any(x => x.Key.Length > 100 || x.Value is null || x.Value.Length > 100)
            || content.Mapping.Resources.Any(x => x.Key.Length > 100 || x.Value is null || x.Value.Length > 1000))
            throw new PortabilityException("Import mappings are missing or exceed reference limits.");
        var errors = new List<string>();
        foreach (var agent in content.Bundle.Agents)
        {
            if (!content.Mapping.Agents.TryGetValue(agent.Id, out var id)) errors.Add($"Map Agent '{agent.Id}' to a registered approved Agent.");
            else await ValidateAgentAsync(actor, id, ct);
        }
        if (content.Mapping.Agents.Keys.Any(key => !content.Bundle.Agents.Any(a => a.Id == key))
            || content.Mapping.DeploymentNames.Keys.Any(key => !content.Bundle.Deployments.Any(d => d.Id == key)))
            errors.Add("Mappings contain unknown logical IDs.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = await db.Set<DeploymentDraftEntity>().Select(x => x.Name).ToListAsync(ct);
        foreach (var d in content.Bundle.Deployments)
        {
            var name = Name(d, content.Mapping);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) errors.Add($"Invalid mapped name for '{d.Id}'.");
            if (!names.Add(name) || existing.Contains(name, StringComparer.OrdinalIgnoreCase)) errors.Add($"Deployment name conflict: '{name}'.");
        }
        foreach (var section in content.Bundle.Sections)
        {
            var p = ResolveParticipants([section.Kind]).Single();
            await RequireSecurityPolicyAsync(actor, p, ct);
            errors.AddRange(await p.ValidateAsync(section.Content, ImportContext(actor, content, created, new Dictionary<string, Guid>()), ct));
        }
        return errors;
    }

    private PortableParticipantImportContext ImportContext(Guid actor, PreviewContent content, IReadOnlyDictionary<string, Guid> created, IDictionary<string, Guid> resources)
    {
        var definitions = content.Bundle.Sections.SelectMany(s => ResolveParticipants([s.Kind]).Single().DefinedResources(s.Content)).ToArray();
        if (definitions.Length > 1000 || definitions.Select(x => x.Id).Distinct().Count() != definitions.Length
            || definitions.Any(d => !content.Bundle.References.Any(r => r.Id == d.Id && r.Type == d.Type)))
            throw new PortabilityException("Section resource definitions must have unique, declared, typed logical references.");
        return new(actor, content.Mapping, created, false, content.Bundle, resources, definitions);
    }

    private async Task CollectReferencesAsync(PortableParticipantContext context, IEnumerable<IPortableConfigurationParticipant> selected,
        List<PortableAgent> agents, Dictionary<Guid, PortableResourceReference> references, CancellationToken ct)
    {
        var agentIds = (Dictionary<Guid, string>)context.AgentReferences!;
        foreach (var participant in selected)
        {
            await RequireSecurityPolicyAsync(context.ActorId, participant, ct);
            foreach (var resource in await participant.CollectReferencesAsync(context, ct))
            {
                if (resource.Type == "deployment" && context.DeploymentReferences!.ContainsKey(resource.Id)) continue;
                if (resource.Type == "agent")
                {
                    await RequireAsync(context.ActorId, "agent.read", "agent", resource.Id, ct);
                    if (!agentIds.ContainsKey(resource.Id))
                    {
                        var logical = "agent-" + agentIds.Count;
                        if (agents.Any(a => a.Id == logical)) logical = "agent-reference-" + agentIds.Count;
                        agentIds.Add(resource.Id, logical); agents.Add(new(logical, resource.Name));
                    }
                }
                else if (!references.ContainsKey(resource.Id))
                    references.Add(resource.Id, new(resource.Type + "-reference-" + references.Count, resource.Type, resource.Name));
            }
        }
    }

    private IPortableConfigurationParticipant[] ResolveParticipants(IReadOnlyList<string> kinds)
    {
        if (kinds.Count > 32 || kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Count) throw new PortabilityException("Select distinct supported sections.");
        if (kinds.Any(kind => string.IsNullOrWhiteSpace(kind) || kind.Length > 100 || kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
            throw new PortabilityException("Section names must be safe logical names of at most 100 characters.");
        if (kinds.Any(kind => kind is "accounts" or "sessions" or "agent-identities" or "password-hashes"))
            throw new PortabilityException("Runtime identities, live sessions and account security state are not portable configuration.");
        return kinds.Select(kind => participants.SingleOrDefault(x => x.Kind == kind)
            ?? throw new PortabilityException($"Unsupported portability section: '{kind}'. No configuration from this section was imported or exported.")).ToArray();
    }
    private async Task RequireSecurityPolicyAsync(Guid actor, IPortableConfigurationParticipant p, CancellationToken ct)
    {
        if ((p.SecurityPolicy || p.Kind is "groups" or "grants" or "security-policy" or "groups-grants")
            && !await db.Accounts.AnyAsync(x => x.Id == actor && x.Enabled && x.Role == AccountRole.SystemAdmin, ct))
            throw new PortabilityAccessException();
    }
    private async Task<PortableImportEntity> RequiredRunAsync(Guid actor, Guid id, CancellationToken ct) =>
        await db.Set<PortableImportEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ActorId == actor, ct)
        ?? throw new PortabilityException("The import preview is unavailable.");
    private PreviewContent ReadContent(PortableImportEntity row)
    {
        try
        {
            var bytes = Protector(row.Id, row.ActorId).Unprotect(row.ProtectedContent);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != row.Fingerprint) throw new PortabilityException("The stored preview fingerprint is invalid.");
            return JsonSerializer.Deserialize<PreviewContent>(bytes, PortableBundleValidator.JsonOptions) ?? throw new PortabilityException("Invalid stored preview.");
        }
        catch (Exception e) when (e is CryptographicException or JsonException) { throw new PortabilityException("The encrypted preview could not be read; check persisted encryption keys."); }
    }
    private PortablePreview Preview(PortableImportEntity row, PreviewContent content, IReadOnlyList<string> conflicts) =>
        new(row.Id, row.Fingerprint, row.ExpiresAt, content.Bundle.Deployments.Select(d => Name(d, content.Mapping)).ToArray(),
            ["Credentials, tokens, environment values and files are plaintext in this bundle. Imported workloads and automation remain inactive. Existing grants are not copied implicitly.",
             "Import uses individually committed configuration services. Interrupted imports are reported as partial, not rolled back or automatically replayed.",
             .. content.Bundle.Deployments.SelectMany(d => PortableBundleValidator.Warnings(d.Compose)).Distinct()],
            conflicts, conflicts.Count == 0 && row.State == "preview" && row.ExpiresAt > clock.GetUtcNow().ToUnixTimeSeconds())
        { Sections = content.Bundle.Sections.OrderBy(s => ResolveParticipants([s.Kind]).Single().Priority).Select(s => s.Kind).ToArray(),
            AgentMappings = content.Mapping.Agents, ResourceMappings = content.Mapping.Resources, References = content.Bundle.References };
    private IDataProtector Protector(Guid id, Guid actor) => protection.CreateProtector("Stanza.PortablePreview.v1", id.ToString("N"), actor.ToString("N"));
    private static string Name(PortableDeployment d, PortableImportMapping mapping) => mapping.DeploymentNames.TryGetValue(d.Id, out var name) ? name : d.Name;
    private Task<int> SaveProgressAsync(Guid id, IReadOnlyDictionary<string, Guid> created, CancellationToken ct) =>
        db.Set<PortableImportEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedDeployments, JsonSerializer.Serialize(created.Values.ToArray())), ct);
    private Task<int> SaveResourceProgressAsync(Guid id, IReadOnlyDictionary<string, Guid> resources, IReadOnlyList<string> completed, string? current, CancellationToken ct) =>
        db.Set<PortableImportEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedResources, JsonSerializer.Serialize(resources))
            .SetProperty(x => x.CompletedSections, JsonSerializer.Serialize(completed)).SetProperty(x => x.CurrentSection, current), ct);
    private Task<int> FinishAsync(Guid id, string state, string? failure, CancellationToken ct) =>
        db.Set<PortableImportEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, state).SetProperty(x => x.Failure, failure).SetProperty(x => x.ProtectedContent, Array.Empty<byte>()), ct);
    private sealed record PreviewContent(PortableBundle Bundle, PortableImportMapping Mapping);
    private static PortableBundle WithSourceReservations(PortableBundle bundle)
    {
        var section = bundle.Sections.SingleOrDefault(s => s.Kind == "sources");
        if (section is null) return bundle;
        var sources = Participants.ParticipantSupport.Read<Participants.PortableSources>(section.Content);
        return bundle with { Deployments = bundle.Deployments.Select(d => d with { SourceDestinations = sources.Bindings.SingleOrDefault(b => b.Deployment == d.Id)?.Git.Select(g => g.Destination).ToArray() ?? [] }).ToArray() };
    }
}
