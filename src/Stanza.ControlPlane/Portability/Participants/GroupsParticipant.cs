using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Security;

namespace Stanza.ControlPlane.Portability.Participants;

public sealed record PortableGrant(string Action, string ResourceType, string? Resource);
public sealed record PortableGroup(string Name, IReadOnlyList<string> Members, IReadOnlyList<PortableGrant> Grants);
public sealed record PortableUserGrants(string Username, IReadOnlyList<PortableGrant> Grants);
public sealed record PortableGroups(IReadOnlyList<PortableGroup> Groups, IReadOnlyList<PortableUserGrants> Users);

public sealed class GroupsParticipant(StanzaDbContext db, AccessService access, ParticipantSupport support) : IPortableConfigurationParticipant
{
    public string Kind => "groups-grants";
    public bool SecurityPolicy => true;
    public int Priority => 100;
    public async Task<IReadOnlyList<PortableResourceCandidate>> CollectReferencesAsync(PortableParticipantContext c, CancellationToken ct)
    {
        await support.SystemAdmin(c.ActorId, ct);
        var grants = await db.Grants.AsNoTracking().Where(g => g.ResourceId != null).ToListAsync(ct);
        var result = new List<PortableResourceCandidate>();
        foreach (var g in grants)
        {
            var id = g.ResourceId!.Value;
            if (!PermissionCatalog.Contains(g.Action, g.ResourceType)) throw new PortabilityException("Resolve unknown permission catalog entries before exporting groups/grants.");
            var name = g.ResourceType switch
            {
                "agent" => await db.Set<AgentEntity>().Where(x => x.Id == id).Select(x => x.Hostname).SingleOrDefaultAsync(ct),
                "deployment" => await db.Set<DeploymentDraftEntity>().Where(x => x.Id == id).Select(x => x.Name).SingleOrDefaultAsync(ct),
                "credential" => await db.Set<SourceCredentialEntity>().Where(x => x.Id == id).Select(x => x.Name).SingleOrDefaultAsync(ct),
                "action" => await db.Set<ActionDefinition>().Where(x => x.Id == id).Select(x => x.Name).SingleOrDefaultAsync(ct),
                _ => null
            } ?? throw new PortabilityException("A grant references an unavailable or nonportable resource. Resolve it before export.");
            await support.Require(c.ActorId, g.ResourceType + ".read", g.ResourceType, id, ct);
            result.Add(new(id, g.ResourceType, name));
        }
        return result;
    }
    public async Task<JsonElement> ExportAsync(PortableParticipantContext c, CancellationToken ct)
    {
        await support.SystemAdmin(c.ActorId, ct);
        var groups = new List<PortableGroup>();
        foreach (var g in await access.ListGroupsAsync(ct))
        {
            var members = new List<string>();
            foreach (var member in g.Members)
            {
                if (!await db.Accounts.AnyAsync(x => x.Id == member && x.Role == AccountRole.User, ct))
                    throw new PortabilityException("Permission group memberships must reference ordinary Users before portability export.");
                members.Add(await support.Username(member, ct));
            }
            groups.Add(new(g.Name, members, g.Grants.Select(x => Grant(c, x.Action, x.ResourceType, x.ResourceId)).ToArray()));
        }
        var users = new List<PortableUserGrants>();
        var direct = await db.Grants.AsNoTracking().Where(x => x.AccountId != null).ToListAsync(ct);
        foreach (var owner in direct.GroupBy(x => x.AccountId!.Value))
        {
            if (!await db.Accounts.AnyAsync(x => x.Id == owner.Key && x.Role == AccountRole.User, ct))
                throw new PortabilityException("Direct grants on privileged accounts are not portable; export ordinary-user configuration only.");
            users.Add(new(await support.Username(owner.Key, ct), owner.Select(x => Grant(c, x.Action, x.ResourceType, x.ResourceId)).ToArray()));
        }
        return ParticipantSupport.Json(new PortableGroups(groups, users));
    }
    private static PortableGrant Grant(PortableParticipantContext c, string action, string type, Guid? id) =>
        new(action, type, id is { } value ? ParticipantSupport.Reference(c, value, type) : null);

    public async Task<IReadOnlyList<string>> ValidateAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await support.SystemAdmin(c.ActorId, ct);
        var section = ParticipantSupport.Read<PortableGroups>(content);
        ParticipantSupport.Bound(section.Groups.Count, 100); ParticipantSupport.Bound(section.Users.Count);
        if (section.Groups.Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != section.Groups.Count
            || section.Users.Select(u => u.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != section.Users.Count)
            throw new PortabilityException("Duplicate groups or account-grant definitions.");
        var existingNames = await db.Groups.Select(x => x.Name).ToListAsync(ct);
        var conflicts = new List<string>();
        foreach (var group in section.Groups)
        {
            var name = Name(group, c);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) throw new PortabilityException("Invalid permission group name.");
            if (existingNames.Contains(name, StringComparer.OrdinalIgnoreCase)) conflicts.Add($"Group name conflict: '{name}'. Supply resource mapping 'group:{group.Name}' to a new name.");
            ParticipantSupport.Bound(group.Members.Count); ParticipantSupport.Bound(group.Grants.Count);
            if (group.Members.Distinct().Count() != group.Members.Count) throw new PortabilityException("Duplicate group members.");
            foreach (var member in group.Members) await support.Owner(c, member, ct, true);
            await ValidateGrants(group.Grants, c, ct);
        }
        foreach (var user in section.Users)
        {
            await support.Owner(c, user.Username, ct, true);
            ParticipantSupport.Bound(user.Grants.Count); await ValidateGrants(user.Grants, c, ct);
        }
        return conflicts;
    }
    private async Task ValidateGrants(IReadOnlyList<PortableGrant> grants, PortableParticipantImportContext c, CancellationToken ct)
    {
        if (grants.Distinct().Count() != grants.Count) throw new PortabilityException("Duplicate permission grants.");
        foreach (var grant in grants)
        {
            if (!PermissionCatalog.Contains(grant.Action, grant.ResourceType) || grant.ResourceType == "instance" && grant.Resource is not null)
                throw new PortabilityException("Unsupported permission action/resource scope in imported grants.");
            if (grant.Resource is { } key) await support.Resolve(c, key, grant.ResourceType, ct, true);
        }
    }
    public async Task ApplyAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        var conflicts = await ValidateAsync(content, c, ct);
        if (conflicts.Count > 0) throw new PortabilityException("Resolve group name conflicts before applying grants.");
        var section = ParticipantSupport.Read<PortableGroups>(content);
        foreach (var group in section.Groups)
        {
            var saved = await access.CreateGroupAsync(c.ActorId, Name(group, c), ct);
            c.CreatedResources!["created-group:" + c.CreatedResources.Count] = saved.Id;
            foreach (var member in group.Members) await access.AddMemberAsync(c.ActorId, saved.Id, await support.Owner(c, member, ct, true), ct);
            foreach (var grant in group.Grants)
                await access.AddGrantAsync(c.ActorId, null, saved.Id, grant.Action, grant.ResourceType,
                    grant.Resource is { } key ? await support.Resolve(c, key, grant.ResourceType, ct) : null, ct);
        }
        foreach (var user in section.Users)
        {
            var owner = await support.Owner(c, user.Username, ct, true);
            foreach (var grant in user.Grants)
            {
                var id = grant.Resource is { } key ? await support.Resolve(c, key, grant.ResourceType, ct) : null;
                if (await db.Grants.AnyAsync(x => x.AccountId == owner && x.Action == grant.Action && x.ResourceType == grant.ResourceType && x.ResourceId == id, ct)) continue;
                await access.AddGrantAsync(c.ActorId, owner, null, grant.Action, grant.ResourceType, id, ct);
            }
        }
    }
    private static string Name(PortableGroup group, PortableParticipantImportContext c) =>
        c.Mapping.Resources.TryGetValue("group:" + group.Name, out var value) ? value : group.Name;
}
