using Microsoft.EntityFrameworkCore;
using Stanza.Core.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Security;

public sealed record GrantSummary(Guid Id, Guid? AccountId, Guid? GroupId, string Action, string ResourceType, Guid? ResourceId);
public sealed record GroupSummary(Guid Id, string Name, IReadOnlyList<Guid> Members, IReadOnlyList<GrantSummary> Grants);
public sealed record EffectivePermissionSummary(string Action, string ResourceType, Guid? ResourceId, bool Allowed,
    IReadOnlyList<string> Sources);
public sealed record AccessSummary(Guid AccountId, AccountRole Role, bool Enabled, IReadOnlyList<GrantSummary> DirectGrants,
    IReadOnlyList<GroupSummary> Groups, IReadOnlyList<EffectivePermissionSummary> EffectivePermissions,
    string Explanation, DateTimeOffset EvaluatedAt);

public sealed class AccessService(StanzaDbContext db, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<GroupSummary>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = await db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var memberships = await db.GroupMemberships.AsNoTracking().ToListAsync(cancellationToken);
        var grants = await db.Grants.AsNoTracking().Where(x => x.GroupId != null).OrderBy(x => x.Action).ToListAsync(cancellationToken);
        return groups.Select(group => new GroupSummary(group.Id, group.Name,
            memberships.Where(x => x.GroupId == group.Id).Select(x => x.AccountId).Order().ToArray(),
            grants.Where(x => x.GroupId == group.Id).Select(Summary).ToArray())).ToArray();
    }

    public async Task<AccessSummary> GetAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await AccountAsync(accountId, cancellationToken);
        var direct = await db.Grants.AsNoTracking().Where(x => x.AccountId == accountId).OrderBy(x => x.Action).ToListAsync(cancellationToken);
        var groups = (await ListGroupsAsync(cancellationToken)).Where(x => x.Members.Contains(accountId)).ToArray();
        var grantScopes = direct.Select(ToPermission).Concat(groups.SelectMany(x => x.Grants).Select(ToPermission));
        if (account.Role is AccountRole.Admin or AccountRole.SystemAdmin)
            grantScopes = grantScopes.Concat(PermissionCatalog.All.Select(x => new PermissionGrant(x.Action, x.ResourceType, null)));
        var effective = grantScopes.Distinct().OrderBy(x => x.Action).ThenBy(x => x.ResourceType).ThenBy(x => x.ResourceId)
            .Select(grant =>
            {
                var decision = Decide(account, grant.Action, grant.ResourceType, grant.ResourceId, direct.Select(ToPermission), groups);
                return new EffectivePermissionSummary(grant.Action, grant.ResourceType, grant.ResourceId, decision.Allowed, decision.Sources);
            }).ToArray();
        var explanation = !account.Enabled ? "Disabled accounts have no access."
            : account.Role == AccountRole.SystemAdmin ? "System Admin: full access, including privileged account management."
            : account.Role == AccountRole.Admin ? "Admin: full operational access; privileged accounts and administrative roles are protected."
            : "User: denied unless an exact direct or group grant permits the action. Grants are additive.";
        return new(accountId, account.Role, account.Enabled, direct.Select(Summary).ToArray(), groups, effective,
            explanation, timeProvider.GetUtcNow());
    }

    public async Task<GroupSummary> CreateGroupAsync(Guid actorId, string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        return await WriteAsync(actorId, async actor =>
        {
            await UniqueNameAsync(name, null, cancellationToken);
            var group = new GroupEntity { Id = Guid.NewGuid(), Name = name };
            db.Groups.Add(group);
            try { await db.SaveChangesAsync(cancellationToken); }
            finally { db.Entry(group).State = EntityState.Detached; }
            return new GroupSummary(group.Id, group.Name, [], []);
        }, cancellationToken);
    }

    public Task RenameGroupAsync(Guid actorId, Guid groupId, string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        return WriteAsync(actorId, async actor =>
        {
            await GroupAsync(groupId, cancellationToken);
            await ProtectGroupAsync(actor, groupId, cancellationToken);
            await UniqueNameAsync(name, groupId, cancellationToken);
            await db.Groups.Where(x => x.Id == groupId).ExecuteUpdateAsync(update => update.SetProperty(x => x.Name, name), cancellationToken);
            await RotateGroupStampsAsync(groupId, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task DeleteGroupAsync(Guid actorId, Guid groupId, CancellationToken cancellationToken = default) =>
        WriteAsync(actorId, async actor =>
        {
            await GroupAsync(groupId, cancellationToken);
            await ProtectGroupAsync(actor, groupId, cancellationToken);
            await RotateGroupStampsAsync(groupId, cancellationToken);
            await db.Groups.Where(x => x.Id == groupId).ExecuteDeleteAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task AddMemberAsync(Guid actorId, Guid groupId, Guid accountId, CancellationToken cancellationToken = default) =>
        WriteAsync(actorId, async actor =>
        {
            await GroupAsync(groupId, cancellationToken);
            await ProtectGroupAsync(actor, groupId, cancellationToken);
            var account = await AccountAsync(accountId, cancellationToken);
            if (account.Role != AccountRole.User)
                throw new SecurityOperationException("Only ordinary Users can join permission groups; administrators already have operational access.");
            if (await db.GroupMemberships.AnyAsync(x => x.AccountId == accountId && x.GroupId == groupId, cancellationToken))
                throw new SecurityOperationException("The account is already a member of this group.");
            var membership = new GroupMembershipEntity { AccountId = accountId, GroupId = groupId };
            db.GroupMemberships.Add(membership);
            try { await db.SaveChangesAsync(cancellationToken); }
            finally { db.Entry(membership).State = EntityState.Detached; }
            await RotateStampAsync(accountId, cancellationToken);
            return true;
        }, cancellationToken);

    public Task RemoveMemberAsync(Guid actorId, Guid groupId, Guid accountId, CancellationToken cancellationToken = default) =>
        WriteAsync(actorId, async actor =>
        {
            await GroupAsync(groupId, cancellationToken);
            await ProtectGroupAsync(actor, groupId, cancellationToken);
            var account = await AccountAsync(accountId, cancellationToken);
            ProtectAccount(actor, account);
            var count = await db.GroupMemberships.Where(x => x.AccountId == accountId && x.GroupId == groupId).ExecuteDeleteAsync(cancellationToken);
            if (count != 1) throw new SecurityOperationException("The account is not a member of this group.");
            await RotateStampAsync(accountId, cancellationToken);
            return true;
        }, cancellationToken);

    public Task<GrantSummary> AddGrantAsync(Guid actorId, Guid? accountId, Guid? groupId, string action,
        string resourceType, Guid? resourceId, CancellationToken cancellationToken = default)
    {
        if (accountId.HasValue == groupId.HasValue)
            throw new SecurityOperationException("Select exactly one account or group for the grant.");
        ValidateScope(action, resourceType, resourceId);
        return WriteAsync(actorId, async actor =>
        {
            await ProtectOwnerAsync(actor, accountId, groupId, cancellationToken);
            if (await db.Grants.AnyAsync(x => x.AccountId == accountId && x.GroupId == groupId
                && x.Action == action && x.ResourceType == resourceType && x.ResourceId == resourceId, cancellationToken))
                throw new SecurityOperationException("This permission grant already exists.");
            var grant = new GrantEntity { Id = Guid.NewGuid(), AccountId = accountId, GroupId = groupId,
                Action = action, ResourceType = resourceType, ResourceId = resourceId };
            db.Grants.Add(grant);
            try { await db.SaveChangesAsync(cancellationToken); }
            finally { db.Entry(grant).State = EntityState.Detached; }
            await RotateOwnerStampsAsync(accountId, groupId, cancellationToken);
            return Summary(grant);
        }, cancellationToken);
    }

    public Task RemoveGrantAsync(Guid actorId, Guid grantId, CancellationToken cancellationToken = default) =>
        WriteAsync(actorId, async actor =>
        {
            var grant = await db.Grants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == grantId, cancellationToken)
                ?? throw new SecurityOperationException("The permission grant is unavailable.");
            await ProtectOwnerAsync(actor, grant.AccountId, grant.GroupId, cancellationToken);
            await db.Grants.Where(x => x.Id == grantId).ExecuteDeleteAsync(cancellationToken);
            await RotateOwnerStampsAsync(grant.AccountId, grant.GroupId, cancellationToken);
            return true;
        }, cancellationToken);

    public async Task<bool> IsAllowedAsync(Guid accountId, string action, string resourceType, Guid? resourceId,
        CancellationToken cancellationToken = default)
    {
        if (!ValidScope(action, resourceType, resourceId)) return false;
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account is null || !account.Enabled) return false;
        // These are operational catalog entries only. Security management uses its own role-checked services.
        if (account.Role is AccountRole.Admin or AccountRole.SystemAdmin) return true;
        if (account.Role != AccountRole.User) return false;
        return await db.Grants.AsNoTracking().AnyAsync(grant => grant.Action == action && grant.ResourceType == resourceType
            && (grant.ResourceId == null || grant.ResourceId == resourceId)
            && (grant.AccountId == accountId || (grant.GroupId != null && db.GroupMemberships.Any(membership =>
                membership.GroupId == grant.GroupId && membership.AccountId == accountId))), cancellationToken);
    }

    private async Task<T> WriteAsync<T>(Guid actorId, Func<AccountEntity, Task<T>> action, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        var actor = await AccountAsync(actorId, ct);
        if (!actor.Enabled || actor.Role is not (AccountRole.Admin or AccountRole.SystemAdmin))
            throw new SecurityOperationException("Only an enabled Admin or System Admin can manage groups and permission grants.");
        var result = await action(actor);
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task ProtectOwnerAsync(AccountEntity actor, Guid? accountId, Guid? groupId, CancellationToken ct)
    {
        if (accountId is { } id) ProtectAccount(actor, await AccountAsync(id, ct));
        if (groupId is { } group)
        {
            await GroupAsync(group, ct);
            await ProtectGroupAsync(actor, group, ct);
        }
    }

    private async Task ProtectGroupAsync(AccountEntity actor, Guid groupId, CancellationToken ct)
    {
        if (actor.Role != AccountRole.SystemAdmin && await db.GroupMemberships.AnyAsync(membership => membership.GroupId == groupId
            && db.Accounts.Any(account => account.Id == membership.AccountId && account.Role != AccountRole.User), ct))
            throw new SecurityOperationException("Only a System Admin can change a group containing a privileged account.");
    }

    private static void ProtectAccount(AccountEntity actor, AccountEntity target)
    {
        if (actor.Role != AccountRole.SystemAdmin && target.Role != AccountRole.User)
            throw new SecurityOperationException("Only a System Admin can change a privileged account's access.");
    }

    private async Task<AccountEntity> AccountAsync(Guid id, CancellationToken ct) =>
        await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new SecurityOperationException("The account is unavailable.");

    private async Task<GroupEntity> GroupAsync(Guid id, CancellationToken ct) =>
        await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new SecurityOperationException("The group is unavailable.");

    private async Task UniqueNameAsync(string name, Guid? excludingId, CancellationToken ct)
    {
        // Normalize with the same invariant comparison on both supported providers, without relying on database collation.
        var names = await db.Groups.AsNoTracking().Where(x => x.Id != excludingId).Select(x => x.Name).ToListAsync(ct);
        if (names.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            throw new SecurityOperationException("That group name is already in use.");
    }

    private Task RotateOwnerStampsAsync(Guid? accountId, Guid? groupId, CancellationToken ct) =>
        accountId is { } account ? RotateStampAsync(account, ct) : RotateGroupStampsAsync(groupId!.Value, ct);

    private Task<int> RotateStampAsync(Guid accountId, CancellationToken ct)
    {
        var stamp = Guid.NewGuid().ToString("N");
        return db.Accounts.Where(x => x.Id == accountId).ExecuteUpdateAsync(update => update.SetProperty(x => x.SecurityStamp, stamp), ct);
    }

    private Task<int> RotateGroupStampsAsync(Guid groupId, CancellationToken ct)
    {
        var stamp = Guid.NewGuid().ToString("N");
        return db.Accounts.Where(account => db.GroupMemberships.Any(member => member.GroupId == groupId && member.AccountId == account.Id))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.SecurityStamp, stamp), ct);
    }

    private static string ValidateName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
            throw new SecurityOperationException("Use a group name of 1–100 characters without control characters.");
        return name;
    }

    private static bool ValidScope(string action, string resourceType, Guid? resourceId) =>
        PermissionCatalog.Contains(action, resourceType) && resourceId != Guid.Empty;

    private static void ValidateScope(string action, string resourceType, Guid? resourceId)
    {
        if (!ValidScope(action, resourceType, resourceId))
            throw new SecurityOperationException("Select a supported action/resource pair and a nonempty resource ID, or all resources of that type.");
    }

    private static PermissionDecision Decide(AccountEntity account, string action, string resourceType, Guid? resourceId,
        IEnumerable<PermissionGrant> direct, IEnumerable<GroupSummary> groups) => !ValidScope(action, resourceType, resourceId)
        ? new PermissionDecision(false, [])
        : PermissionEvaluator.Evaluate(account.Role, account.Enabled, action, resourceType, resourceId, direct,
            groups.Select(group => new PermissionGroup(group.Id, group.Name, group.Grants.Select(ToPermission).ToArray())));

    private static PermissionGrant ToPermission(GrantEntity grant) => new(grant.Action, grant.ResourceType, grant.ResourceId);
    private static PermissionGrant ToPermission(GrantSummary grant) => new(grant.Action, grant.ResourceType, grant.ResourceId);
    private static GrantSummary Summary(GrantEntity grant) => new(grant.Id, grant.AccountId, grant.GroupId, grant.Action, grant.ResourceType, grant.ResourceId);
}
