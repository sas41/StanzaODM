using Stanza.Core.Security;

namespace Stanza.Tests.Security;

public sealed class PermissionCatalogTests
{
    [Fact]
    public void CatalogContainsUniqueBoundedExactActionResourcePairsAndNoManagementDelegation()
    {
        Assert.NotEmpty(PermissionCatalog.All);
        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.All.Select(x => (x.Action, x.ResourceType)).Distinct().Count());
        Assert.All(PermissionCatalog.All, entry =>
        {
            Assert.InRange(entry.Action.Length, 1, 100);
            Assert.InRange(entry.ResourceType.Length, 1, 100);
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.DoesNotContain('*', entry.Action);
            Assert.DoesNotContain('*', entry.ResourceType);
            Assert.True(PermissionCatalog.Contains(entry.Action, entry.ResourceType));
        });
        Assert.DoesNotContain(PermissionCatalog.All, entry => entry.ResourceType is "account" or "group" or "role");
    }

    [Theory]
    [InlineData("*", "*")]
    [InlineData("deployment.read", "agent")]
    [InlineData("DEPLOYMENT.READ", "deployment")]
    [InlineData("deployment.read ", "deployment")]
    [InlineData("account.manage", "account")]
    public void UnknownPairsAreNotAccepted(string action, string resourceType)
    {
        Assert.False(PermissionCatalog.Contains(action, resourceType));
    }
}
