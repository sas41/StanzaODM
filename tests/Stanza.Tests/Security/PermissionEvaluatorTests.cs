using Stanza.Core.Security;

namespace Stanza.Tests.Security;

public sealed class PermissionEvaluatorTests
{
    private static readonly Guid Deployment = Guid.NewGuid();
    private static readonly PermissionGrant Read = new("deployment.read", "deployment", Deployment);

    [Fact]
    public void UsersAreDefaultDeny()
    {
        Assert.False(Evaluate(AccountRole.User).Allowed);
    }

    [Theory]
    [InlineData(AccountRole.Admin)]
    [InlineData(AccountRole.SystemAdmin)]
    public void AdministratorsBypassOperationalGrants(AccountRole role)
    {
        Assert.True(Evaluate(role).Allowed);
    }

    [Theory]
    [InlineData(AccountRole.User)]
    [InlineData(AccountRole.Admin)]
    [InlineData(AccountRole.SystemAdmin)]
    public void DisabledAccountsAreDeniedEvenWithGrants(AccountRole role)
    {
        Assert.False(PermissionEvaluator.Evaluate(role, false, Read.Action, Read.ResourceType, Deployment, [Read], []).Allowed);
    }

    [Theory]
    [InlineData(PermissionOperation.ManagePrivilegedAccount)]
    [InlineData(PermissionOperation.GrantAdministrativeRole)]
    public void ProtectedManagementCannotBeGrantedOrBypassedByAdmins(PermissionOperation operation)
    {
        foreach (var role in new[] { AccountRole.User, AccountRole.Admin })
            Assert.False(PermissionEvaluator.Evaluate(role, true, Read.Action, Read.ResourceType, Deployment, [Read], [], operation).Allowed);
        Assert.True(PermissionEvaluator.Evaluate(AccountRole.SystemAdmin, true, Read.Action, Read.ResourceType,
            Deployment, [], [], operation).Allowed);
    }

    [Fact]
    public void OrdinaryAccountManagementRequiresAnAdministratorNotDelegatedGrants()
    {
        Assert.False(PermissionEvaluator.Evaluate(AccountRole.User, true, Read.Action, Read.ResourceType,
            Deployment, [Read], [], PermissionOperation.ManageOrdinaryAccount).Allowed);
        Assert.True(PermissionEvaluator.Evaluate(AccountRole.Admin, true, Read.Action, Read.ResourceType,
            Deployment, [], [], PermissionOperation.ManageOrdinaryAccount).Allowed);
    }

    [Fact]
    public void DirectAndGroupGrantsAreAdditiveWithExplanations()
    {
        var groups = new[] { new PermissionGroup(Guid.NewGuid(), "Readers", [Read]), new PermissionGroup(Guid.NewGuid(), "Empty", []) };
        var decision = PermissionEvaluator.Evaluate(AccountRole.User, true, Read.Action, Read.ResourceType, Deployment, [Read], groups);
        Assert.True(decision.Allowed);
        Assert.Equal(new[] { "Direct grant", "Group: Readers" }, decision.Sources);
        Assert.True(PermissionEvaluator.Evaluate(AccountRole.User, true, Read.Action, Read.ResourceType, Deployment, [], groups).Allowed);
    }

    [Fact]
    public void ExactActionResourceTypeAndIdAreRequired()
    {
        Assert.False(PermissionEvaluator.Evaluate(AccountRole.User, true, "deployment.write", "deployment", Deployment, [Read], []).Allowed);
        Assert.False(PermissionEvaluator.Evaluate(AccountRole.User, true, Read.Action, "agent", Deployment, [Read], []).Allowed);
        Assert.False(PermissionEvaluator.Evaluate(AccountRole.User, true, Read.Action, Read.ResourceType, Guid.NewGuid(), [Read], []).Allowed);
        Assert.False(PermissionEvaluator.Evaluate(AccountRole.User, true, Read.Action.ToUpperInvariant(), Read.ResourceType, Deployment, [Read], []).Allowed);
        Assert.False(PermissionEvaluator.Evaluate(AccountRole.User, true, Read.Action, Read.ResourceType, null, [Read], []).Allowed);
    }

    [Fact]
    public void TypeWideGrantDoesNotBecomeAnActionOrCrossTypeWildcard()
    {
        PermissionGrant grant = new("deployment.read", "deployment", null);
        Assert.True(PermissionEvaluator.Evaluate(AccountRole.User, true, Read.Action, "deployment", Guid.NewGuid(), [grant], []).Allowed);
        Assert.False(PermissionEvaluator.Evaluate(AccountRole.User, true, "deployment.write", "deployment", Deployment, [grant], []).Allowed);
        Assert.False(PermissionEvaluator.Evaluate(AccountRole.User, true, Read.Action, "agent", Deployment, [grant], []).Allowed);
    }

    [Theory]
    [InlineData(AccountRole.User)]
    [InlineData(AccountRole.SystemAdmin)]
    public void InvalidRequestsDenyRatherThanBypass(AccountRole role)
    {
        Assert.False(PermissionEvaluator.Evaluate(role, true, "", "deployment", Deployment, [Read], []).Allowed);
        Assert.False(PermissionEvaluator.Evaluate(role, true, Read.Action, "", Deployment, [Read], []).Allowed);
        Assert.False(PermissionEvaluator.Evaluate(role, true, Read.Action, Read.ResourceType, Deployment, [Read], [], (PermissionOperation)99).Allowed);
        Assert.False(Evaluate((AccountRole)99).Allowed);
    }

    private static PermissionDecision Evaluate(AccountRole role) =>
        PermissionEvaluator.Evaluate(role, true, Read.Action, Read.ResourceType, Deployment, [], []);
}
