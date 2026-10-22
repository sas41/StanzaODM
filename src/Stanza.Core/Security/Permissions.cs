namespace Stanza.Core.Security;

public enum AccountRole { User, Admin, SystemAdmin }

public enum PermissionOperation
{
    Operational,
    ManageOrdinaryAccount,
    ManagePrivilegedAccount,
    GrantAdministrativeRole
}

public sealed record PermissionGrant(string Action, string ResourceType, Guid? ResourceId);
public sealed record PermissionGroup(Guid Id, string Name, IReadOnlyList<PermissionGrant> Grants);
public sealed record PermissionDecision(bool Allowed, IReadOnlyList<string> Sources);

/// <summary>Exact action/resource matching; a null grant resource ID covers that resource type.</summary>
public static class PermissionEvaluator
{
    public static PermissionDecision Evaluate(AccountRole role, bool enabled, string action,
        string resourceType, Guid? resourceId, IEnumerable<PermissionGrant> directGrants,
        IEnumerable<PermissionGroup> groups, PermissionOperation operation = PermissionOperation.Operational)
    {
        if (!enabled || !Enum.IsDefined(role) || !Enum.IsDefined(operation)
            || string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(resourceType))
            return new(false, []);

        if (role == AccountRole.SystemAdmin)
            return new(true, ["System Admin"]);
        if (operation is PermissionOperation.ManagePrivilegedAccount or PermissionOperation.GrantAdministrativeRole)
            return new(false, []);
        if (role == AccountRole.Admin)
            return new(true, ["Admin"]);
        // Delegated operational grants must never become an indirect account-management path.
        if (operation != PermissionOperation.Operational)
            return new(false, []);

        bool Matches(PermissionGrant grant) => grant.Action == action && grant.ResourceType == resourceType
            && (grant.ResourceId is null || grant.ResourceId == resourceId);
        var sources = new List<string>();
        if (directGrants.Any(Matches)) sources.Add("Direct grant");
        sources.AddRange(groups.Where(group => group.Grants.Any(Matches)).Select(group => $"Group: {group.Name}"));
        return new(sources.Count > 0, sources);
    }
}
