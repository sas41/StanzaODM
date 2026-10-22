using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Security;

namespace Stanza.ControlPlane.Portability.Participants;

public sealed class ParticipantSupport(StanzaDbContext db, AccessService access)
{
    public static T Read<T>(JsonElement content)
    {
        try { RejectNullArrayEntries(content); return content.Deserialize<T>(PortableBundleValidator.JsonOptions) ?? throw new PortabilityException("A section object is required."); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { throw new PortabilityException("Invalid configuration section schema."); }
    }
    private static void RejectNullArrayEntries(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null) throw new PortabilityException("Null entries are not valid configuration resources.");
                RejectNullArrayEntries(item);
            }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) RejectNullArrayEntries(property.Value);
    }
    public static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, PortableBundleValidator.JsonOptions);
    public async Task Require(Guid actor, string action, string type, Guid? id, CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(actor, action, type, id, ct)) throw new PortabilityAccessException();
    }
    public async Task SystemAdmin(Guid actor, CancellationToken ct)
    {
        if (!await db.Accounts.AnyAsync(x => x.Id == actor && x.Enabled && x.Role == AccountRole.SystemAdmin, ct)) throw new PortabilityAccessException();
    }
    public static string Reference(PortableParticipantContext context, Guid id, string type) =>
        type == "deployment" && context.DeploymentReferences?.TryGetValue(id, out var deployment) == true ? deployment
        : type == "agent" && context.AgentReferences?.TryGetValue(id, out var agent) == true ? agent
        : context.ResourceReferences?.TryGetValue(id, out var resource) == true && resource.Type == type ? resource.Id
        : throw new PortabilityException("An exported resource reference was not collected.");
    public bool Pending(PortableParticipantImportContext c, string key, string type) =>
        type == "deployment" && c.Bundle?.Deployments.Any(d => d.Id == key) == true
        || c.Definitions?.Any(r => r.Id == key && r.Type == type) == true;
    public async Task<Guid?> Resolve(PortableParticipantImportContext c, string key, string type, CancellationToken ct, bool preview = false)
    {
        if (c.Bundle is { } bundle && !(type == "deployment" && bundle.Deployments.Any(d => d.Id == key))
            && !(type == "agent" && bundle.Agents.Any(a => a.Id == key)) && !bundle.References.Any(r => r.Id == key && r.Type == type)
            && c.Definitions?.Any(r => r.Id == key && r.Type == type) != true)
            throw new PortabilityException($"Undeclared or mismatched {type} reference '{key}'.");
        Guid id;
        if (type == "deployment" && c.CreatedDeployments.TryGetValue(key, out id)) return id;
        if (c.CreatedResources?.TryGetValue(key, out id) == true) return id;
        if (type == "agent")
        {
            if (!c.Mapping.Agents.TryGetValue(key, out id)) throw new PortabilityException($"Map Agent reference '{key}'.");
        }
        else if (Pending(c, key, type))
        {
            if (preview) return null;
            throw new PortabilityException($"Referenced {type} '{key}' has not been created yet.");
        }
        else if (!c.Mapping.Resources.TryGetValue(key, out var mapped) || !Guid.TryParse(mapped, out id))
            throw new PortabilityException($"Map external {type} reference '{key}' to an existing authorized resource.");
        var exists = type switch
        {
            "agent" => await db.Set<AgentEntity>().AnyAsync(x => x.Id == id && x.Status == "approved", ct),
            "deployment" => await db.Set<Deployments.DeploymentDraftEntity>().AnyAsync(x => x.Id == id, ct),
            "credential" => await db.Set<SourceCredentialEntity>().AnyAsync(x => x.Id == id, ct),
            "action" => await db.Set<ActionDefinition>().AnyAsync(x => x.Id == id, ct),
            _ => false
        };
        if (!exists) throw new PortabilityException($"Mapped {type} '{key}' is unavailable or unapproved.");
        await Require(c.ActorId, type switch { "agent" => "agent.read", "deployment" => "deployment.read", "credential" => "credential.read", _ => "action.read" }, type, id, ct);
        return id;
    }
    public async Task<Guid> Owner(PortableParticipantImportContext c, string username, CancellationToken ct, bool ordinaryOnly = false)
    {
        var key = "account:" + username;
        var mapped = c.Mapping.Resources.TryGetValue(key, out var value) ? value : username;
        if (string.IsNullOrWhiteSpace(mapped) || mapped.Length > 100) throw new PortabilityException("Map account usernames explicitly to existing enabled accounts.");
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Username == mapped && x.Enabled, ct)
            ?? throw new PortabilityException($"Existing account mapping required for username '{username}'. Use resource mapping '{key}'. No accounts are created by configuration import.");
        if (ordinaryOnly && account.Role != AccountRole.User) throw new PortabilityException("Group memberships can map only to existing ordinary Users.");
        if (account.Role != AccountRole.User && account.Id != c.ActorId) await SystemAdmin(c.ActorId, ct);
        return account.Id;
    }
    public async Task<string> Username(Guid id, CancellationToken ct) =>
        await db.Accounts.Where(x => x.Id == id).Select(x => x.Username).SingleOrDefaultAsync(ct)
        ?? throw new PortabilityException("A configuration owner account is unavailable; resolve ownership before export.");
    public static void Bound(int count, int maximum = 500)
    { if (count > maximum) throw new PortabilityException("Configuration section exceeds its resource limit."); }
}
