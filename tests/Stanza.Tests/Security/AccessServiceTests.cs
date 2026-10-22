using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Security;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Security;

public sealed class AccessServiceTests : IAsyncLifetime
{
    private const string Password = "a-secure-password-123";
    private static readonly Guid Deployment = Guid.NewGuid();
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StanzaDbContext db = null!;
    private AccountService accounts = null!;
    private AccessService access = null!;
    private AccountSummary root = null!;
    private AccountSummary admin = null!;
    private AccountSummary user = null!;
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        accounts = new(db, TimeProvider.System);
        access = new(db, new FixedClock());
        root = await accounts.BootstrapAsync("root", Password);
        admin = await accounts.CreateAsync(root.Id, "admin", Password, AccountRole.Admin);
        user = await accounts.CreateAsync(root.Id, "user", Password, AccountRole.User);
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task GroupCrudTrimsNamesEnforcesCaseInsensitiveUniquenessAndListsMembersAndGrants()
    {
        Assert.Empty(await access.ListGroupsAsync());
        var group = await access.CreateGroupAsync(admin.Id, "  Operators  ");
        Assert.Equal("Operators", group.Name);
        Assert.Empty(group.Members);
        Assert.Empty(group.Grants);
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.CreateGroupAsync(root.Id, " operators "));
        await access.AddMemberAsync(admin.Id, group.Id, user.Id);
        var grant = await access.AddGrantAsync(admin.Id, null, group.Id, "deployment.read", "deployment", Deployment);
        var listed = Assert.Single(await access.ListGroupsAsync());
        Assert.Equal(user.Id, Assert.Single(listed.Members));
        Assert.Equal(grant, Assert.Single(listed.Grants));
        var beforeRename = (await accounts.FindAsync(user.Id))!.SecurityStamp;
        await access.RenameGroupAsync(admin.Id, group.Id, "  Readers  ");
        Assert.Equal("Readers", Assert.Single(await access.ListGroupsAsync()).Name);
        Assert.NotEqual(beforeRename, (await accounts.FindAsync(user.Id))!.SecurityStamp);
        var beforeDelete = (await accounts.FindAsync(user.Id))!.SecurityStamp;
        await access.DeleteGroupAsync(admin.Id, group.Id);
        Assert.Empty(await access.ListGroupsAsync());
        Assert.Empty(await db.GroupMemberships.ToListAsync());
        Assert.Empty(await db.Grants.ToListAsync());
        Assert.NotEqual(beforeDelete, (await accounts.FindAsync(user.Id))!.SecurityStamp);
        Assert.False(await AllowedAsync(user.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    public async Task InvalidGroupNamesAreRejectedForCreateAndRename(string name)
    {
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.CreateGroupAsync(admin.Id, name));
        var group = await access.CreateGroupAsync(admin.Id, "Valid");
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RenameGroupAsync(admin.Id, group.Id, name));
        Assert.Equal("Valid", Assert.Single(await access.ListGroupsAsync()).Name);
    }

    [Fact]
    public async Task GroupNameBoundsAndRenameCollisionsAreEnforced()
    {
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.CreateGroupAsync(root.Id, new string('x', 101)));
        var first = await access.CreateGroupAsync(root.Id, new string('x', 100));
        var second = await access.CreateGroupAsync(root.Id, "Second");
        await access.RenameGroupAsync(root.Id, first.Id, first.Name.ToUpperInvariant());
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RenameGroupAsync(root.Id, second.Id, first.Name));
    }

    [Fact]
    public async Task MembershipCrudIsValidatedAndRotatesStampOnlyForAffectedAccount()
    {
        var group = await access.CreateGroupAsync(admin.Id, "Readers");
        var other = await accounts.CreateAsync(root.Id, "other", Password, AccountRole.User);
        await access.AddMemberAsync(admin.Id, group.Id, user.Id);
        var joinedStamp = (await accounts.FindAsync(user.Id))!.SecurityStamp;
        Assert.NotEqual(user.SecurityStamp, joinedStamp);
        Assert.Equal(other.SecurityStamp, (await accounts.FindAsync(other.Id))!.SecurityStamp);
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddMemberAsync(admin.Id, group.Id, user.Id));
        Assert.Equal(joinedStamp, (await accounts.FindAsync(user.Id))!.SecurityStamp);
        await access.RemoveMemberAsync(admin.Id, group.Id, user.Id);
        Assert.Empty(Assert.Single(await access.ListGroupsAsync()).Members);
        Assert.NotEqual(joinedStamp, (await accounts.FindAsync(user.Id))!.SecurityStamp);
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RemoveMemberAsync(admin.Id, group.Id, user.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddMemberAsync(admin.Id, group.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddMemberAsync(admin.Id, Guid.NewGuid(), user.Id));
    }

    [Fact]
    public async Task GroupAndDirectPermissionsAreAdditiveAndRevocationIsImmediatelyVisible()
    {
        Assert.False(await AllowedAsync(user.Id));
        var group = await access.CreateGroupAsync(admin.Id, "Readers");
        var groupGrant = await access.AddGrantAsync(admin.Id, null, group.Id, "deployment.read", "deployment", Deployment);
        Assert.False(await AllowedAsync(user.Id));
        await access.AddMemberAsync(admin.Id, group.Id, user.Id);
        Assert.True(await AllowedAsync(user.Id));
        var directGrant = await access.AddGrantAsync(admin.Id, user.Id, null, "deployment.read", "deployment", Deployment);
        var summary = await access.GetAccessAsync(user.Id);
        Assert.Equal(Now, summary.EvaluatedAt);
        Assert.Equal(user.Id, summary.AccountId);
        Assert.Equal(directGrant, Assert.Single(summary.DirectGrants));
        Assert.Equal(group.Id, Assert.Single(summary.Groups).Id);
        var effective = Assert.Single(summary.EffectivePermissions);
        Assert.True(effective.Allowed);
        Assert.Equal(new[] { "Direct grant", "Group: Readers" }, effective.Sources);
        Assert.Contains("additive", summary.Explanation);
        var beforeGroupRevoke = (await accounts.FindAsync(user.Id))!.SecurityStamp;
        await access.RemoveGrantAsync(admin.Id, groupGrant.Id);
        Assert.NotEqual(beforeGroupRevoke, (await accounts.FindAsync(user.Id))!.SecurityStamp);
        Assert.True(await AllowedAsync(user.Id));
        var beforeDirectRevoke = (await accounts.FindAsync(user.Id))!.SecurityStamp;
        await access.RemoveGrantAsync(admin.Id, directGrant.Id);
        Assert.NotEqual(beforeDirectRevoke, (await accounts.FindAsync(user.Id))!.SecurityStamp);
        Assert.False(await AllowedAsync(user.Id));
        Assert.Empty((await access.GetAccessAsync(user.Id)).EffectivePermissions);
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RemoveGrantAsync(admin.Id, directGrant.Id));
    }

    [Fact]
    public async Task ScopedGrantCannotCrossActionResourceTypeOrResourceId()
    {
        await access.AddGrantAsync(admin.Id, user.Id, null, "deployment.read", "deployment", Deployment);
        Assert.True(await AllowedAsync(user.Id));
        Assert.False(await access.IsAllowedAsync(user.Id, "deployment.read", "deployment", Guid.NewGuid()));
        Assert.False(await access.IsAllowedAsync(user.Id, "deployment.read", "deployment", null));
        Assert.False(await access.IsAllowedAsync(user.Id, "deployment.write", "deployment", Deployment));
        Assert.False(await access.IsAllowedAsync(user.Id, "deployment.read", "agent", Deployment));
    }

    [Fact]
    public async Task NullResourceIdCoversOnlyTheExactActionAndResourceType()
    {
        await access.AddGrantAsync(admin.Id, user.Id, null, "environment.read", "deployment", null);
        Assert.True(await access.IsAllowedAsync(user.Id, "environment.read", "deployment", Deployment));
        Assert.True(await access.IsAllowedAsync(user.Id, "environment.read", "deployment", Guid.NewGuid()));
        Assert.False(await access.IsAllowedAsync(user.Id, "environment.reveal", "deployment", Deployment));
        Assert.False(await access.IsAllowedAsync(user.Id, "environment.read", "agent", Deployment));
    }

    [Theory]
    [InlineData("*", "deployment")]
    [InlineData("deployment.read", "*")]
    [InlineData("deployment.*", "deployment")]
    [InlineData("deployment.read", "agent")]
    [InlineData("Deployment.Read", "deployment")]
    [InlineData("account.manage", "account")]
    [InlineData("group.manage", "group")]
    [InlineData("role.grant", "account")]
    [InlineData("", "deployment")]
    public async Task UnknownOrSecurityManagementGrantsAreRejectedEvenForSystemAdmin(string action, string resourceType)
    {
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(root.Id, user.Id, null, action, resourceType, Deployment));
        Assert.False(await access.IsAllowedAsync(root.Id, action, resourceType, Deployment));
        Assert.Empty(await db.Grants.ToListAsync());
    }

    [Fact]
    public async Task GrantOwnerMustBeExactlyOneExistingAccountOrGroupAndResourceIdsCannotBeEmpty()
    {
        var group = await access.CreateGroupAsync(root.Id, "Readers");
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(root.Id, null, null, "deployment.read", "deployment", null));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(root.Id, user.Id, group.Id, "deployment.read", "deployment", null));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(root.Id, Guid.NewGuid(), null, "deployment.read", "deployment", null));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(root.Id, null, Guid.NewGuid(), "deployment.read", "deployment", null));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(root.Id, user.Id, null, "deployment.read", "deployment", Guid.Empty));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(root.Id, user.Id, null, new string('x', 101), "deployment", null));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(root.Id, user.Id, null, "deployment.read", new string('x', 101), null));
        Assert.False(await access.IsAllowedAsync(root.Id, "deployment.read", "deployment", Guid.Empty));
    }

    [Fact]
    public async Task DuplicateGrantsFailWithoutRotatingStampOrRemovingOtherScopes()
    {
        var grant = await access.AddGrantAsync(admin.Id, user.Id, null, "deployment.read", "deployment", Deployment);
        var stamp = (await accounts.FindAsync(user.Id))!.SecurityStamp;
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(admin.Id, user.Id, null, "deployment.read", "deployment", Deployment));
        Assert.Equal(stamp, (await accounts.FindAsync(user.Id))!.SecurityStamp);
        var wide = await access.AddGrantAsync(admin.Id, user.Id, null, "deployment.read", "deployment", null);
        await access.RemoveGrantAsync(admin.Id, grant.Id);
        Assert.Equal(wide, Assert.Single((await access.GetAccessAsync(user.Id)).DirectGrants));
        Assert.True(await AllowedAsync(user.Id));
    }

    [Fact]
    public async Task UsersCannotMutateAnyAccessPathEvenWithOperationalPermissions()
    {
        var group = await access.CreateGroupAsync(root.Id, "Readers");
        var grant = await access.AddGrantAsync(root.Id, user.Id, null, "settings.write", "instance", null);
        await access.AddMemberAsync(root.Id, group.Id, user.Id);
        await AssertAllMutationsDeniedAsync(user.Id, group.Id, grant.Id);
        Assert.True(await access.IsAllowedAsync(user.Id, "settings.write", "instance", null));
        await Assert.ThrowsAsync<SecurityOperationException>(() => accounts.SetRoleAsync(user.Id, user.Id, AccountRole.SystemAdmin));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledAdministratorsCannotMutateGroupsOrGrants(bool systemAdmin)
    {
        var actor = systemAdmin ? await accounts.CreateAsync(root.Id, "second-root", Password, AccountRole.SystemAdmin) : admin;
        var group = await access.CreateGroupAsync(root.Id, "Readers");
        var grant = await access.AddGrantAsync(root.Id, user.Id, null, "deployment.read", "deployment", Deployment);
        await accounts.SetEnabledAsync(root.Id, actor.Id, false);
        await AssertAllMutationsDeniedAsync(actor.Id, group.Id, grant.Id);
        Assert.False(await AllowedAsync(actor.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminCannotChangeDirectGrantsOfPrivilegedAccounts(bool targetSystemAdmin)
    {
        var target = targetSystemAdmin ? root : admin;
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(admin.Id, target.Id, null, "deployment.read", "deployment", Deployment));
        var grant = await access.AddGrantAsync(root.Id, target.Id, null, "deployment.read", "deployment", Deployment);
        var stamp = (await accounts.FindAsync(target.Id))!.SecurityStamp;
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RemoveGrantAsync(admin.Id, grant.Id));
        Assert.Equal(stamp, (await accounts.FindAsync(target.Id))!.SecurityStamp);
        await access.RemoveGrantAsync(root.Id, grant.Id);
        Assert.NotEqual(stamp, (await accounts.FindAsync(target.Id))!.SecurityStamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeitherAdminNorSystemAdminCanAddPrivilegedMembers(bool systemActor)
    {
        var group = await access.CreateGroupAsync(root.Id, "Readers");
        var actor = systemActor ? root : admin;
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddMemberAsync(actor.Id, group.Id, root.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddMemberAsync(actor.Id, group.Id, admin.Id));
        Assert.Empty(Assert.Single(await access.ListGroupsAsync()).Members);
    }

    [Fact]
    public async Task AdminCannotAffectPrivilegedMembersThroughAnyGroupMutation()
    {
        var group = await access.CreateGroupAsync(root.Id, "Protected");
        // A persisted privileged membership must still be protected even if introduced outside this service.
        db.GroupMemberships.Add(new() { AccountId = root.Id, GroupId = group.Id });
        await db.SaveChangesAsync();
        var grant = await access.AddGrantAsync(root.Id, null, group.Id, "deployment.read", "deployment", Deployment);
        var stamp = (await accounts.FindAsync(root.Id))!.SecurityStamp;
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RenameGroupAsync(admin.Id, group.Id, "Renamed"));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.DeleteGroupAsync(admin.Id, group.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddMemberAsync(admin.Id, group.Id, user.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RemoveMemberAsync(admin.Id, group.Id, root.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(admin.Id, null, group.Id, "deployment.start", "deployment", Deployment));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RemoveGrantAsync(admin.Id, grant.Id));
        Assert.Equal(stamp, (await accounts.FindAsync(root.Id))!.SecurityStamp);
        await access.RemoveMemberAsync(root.Id, group.Id, root.Id);
        await access.RenameGroupAsync(admin.Id, group.Id, "Unprotected");
    }

    [Theory]
    [InlineData(AccountRole.Admin)]
    [InlineData(AccountRole.SystemAdmin)]
    public async Task PromotionRemovesDelegatedAccessBeforeAccountBecomesPrivileged(AccountRole role)
    {
        var group = await access.CreateGroupAsync(admin.Id, "Readers");
        await access.AddMemberAsync(admin.Id, group.Id, user.Id);
        await access.AddGrantAsync(admin.Id, user.Id, null, "deployment.read", "deployment", Deployment);
        await accounts.SetRoleAsync(root.Id, user.Id, role);
        Assert.Empty((await access.GetAccessAsync(user.Id)).Groups);
        Assert.Empty((await access.GetAccessAsync(user.Id)).DirectGrants);
        var stamp = (await accounts.FindAsync(user.Id))!.SecurityStamp;
        await access.AddGrantAsync(admin.Id, null, group.Id, "deployment.start", "deployment", Deployment);
        Assert.Equal(stamp, (await accounts.FindAsync(user.Id))!.SecurityStamp);
        await accounts.SetRoleAsync(root.Id, user.Id, AccountRole.User);
        Assert.False(await AllowedAsync(user.Id));
    }

    [Fact]
    public async Task DisabledUsersHaveNoEffectiveAccessEvenWithStoredGrantsAndMemberships()
    {
        var group = await access.CreateGroupAsync(admin.Id, "Readers");
        await access.AddMemberAsync(admin.Id, group.Id, user.Id);
        await access.AddGrantAsync(admin.Id, null, group.Id, "deployment.read", "deployment", Deployment);
        await accounts.SetEnabledAsync(admin.Id, user.Id, false);
        Assert.False(await AllowedAsync(user.Id));
        var summary = await access.GetAccessAsync(user.Id);
        Assert.False(Assert.Single(summary.EffectivePermissions).Allowed);
        Assert.Contains("Disabled", summary.Explanation);
        await accounts.SetEnabledAsync(admin.Id, user.Id, true);
        Assert.True(await AllowedAsync(user.Id));
        await access.RemoveMemberAsync(admin.Id, group.Id, user.Id);
        Assert.False(await AllowedAsync(user.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdministratorEffectiveViewExplainsOperationalBypassWithoutStoredGrants(bool systemAdmin)
    {
        var actor = systemAdmin ? root : admin;
        Assert.True(await AllowedAsync(actor.Id));
        var summary = await access.GetAccessAsync(actor.Id);
        Assert.Empty(summary.DirectGrants);
        Assert.Empty(summary.Groups);
        Assert.Equal(PermissionCatalog.All.Count, summary.EffectivePermissions.Count);
        Assert.All(summary.EffectivePermissions, grant =>
        {
            Assert.True(grant.Allowed);
            Assert.Equal(systemAdmin ? "System Admin" : "Admin", Assert.Single(grant.Sources));
        });
    }

    [Fact]
    public async Task MissingResourcesHaveSafeFailuresAndMissingAccountsAreDenied()
    {
        Assert.False(await AllowedAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.GetAccessAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RenameGroupAsync(root.Id, Guid.NewGuid(), "Readers"));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.DeleteGroupAsync(root.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RemoveMemberAsync(root.Id, Guid.NewGuid(), user.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RemoveGrantAsync(root.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.CreateGroupAsync(Guid.NewGuid(), "Readers"));
    }

    [Fact]
    public async Task RevocationIsObservedByAnotherContextWithoutCachedPermissions()
    {
        var grant = await access.AddGrantAsync(admin.Id, user.Id, null, "deployment.read", "deployment", Deployment);
        await using var otherDb = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(connection).Options);
        var otherService = new AccessService(otherDb, TimeProvider.System);
        Assert.True(await otherService.IsAllowedAsync(user.Id, "deployment.read", "deployment", Deployment));
        await access.RemoveGrantAsync(admin.Id, grant.Id);
        Assert.False(await otherService.IsAllowedAsync(user.Id, "deployment.read", "deployment", Deployment));
    }

    [Fact]
    public async Task GroupGrantChangesRotateAllMemberStampsButNotUnrelatedAccounts()
    {
        var group = await access.CreateGroupAsync(admin.Id, "Readers");
        var second = await accounts.CreateAsync(root.Id, "second", Password, AccountRole.User);
        var unrelated = await accounts.CreateAsync(root.Id, "unrelated", Password, AccountRole.User);
        await access.AddMemberAsync(admin.Id, group.Id, user.Id);
        await access.AddMemberAsync(admin.Id, group.Id, second.Id);
        var userStamp = (await accounts.FindAsync(user.Id))!.SecurityStamp;
        var secondStamp = (await accounts.FindAsync(second.Id))!.SecurityStamp;
        var grant = await access.AddGrantAsync(admin.Id, null, group.Id, "deployment.read", "deployment", Deployment);
        Assert.NotEqual(userStamp, (await accounts.FindAsync(user.Id))!.SecurityStamp);
        Assert.NotEqual(secondStamp, (await accounts.FindAsync(second.Id))!.SecurityStamp);
        Assert.Equal(unrelated.SecurityStamp, (await accounts.FindAsync(unrelated.Id))!.SecurityStamp);
        Assert.Equal(admin.SecurityStamp, (await accounts.FindAsync(admin.Id))!.SecurityStamp);
        userStamp = (await accounts.FindAsync(user.Id))!.SecurityStamp;
        secondStamp = (await accounts.FindAsync(second.Id))!.SecurityStamp;
        await access.RemoveGrantAsync(admin.Id, grant.Id);
        Assert.NotEqual(userStamp, (await accounts.FindAsync(user.Id))!.SecurityStamp);
        Assert.NotEqual(secondStamp, (await accounts.FindAsync(second.Id))!.SecurityStamp);
        Assert.Equal(unrelated.SecurityStamp, (await accounts.FindAsync(unrelated.Id))!.SecurityStamp);
        Assert.False(await AllowedAsync(user.Id));
        Assert.False(await AllowedAsync(second.Id));
    }

    [Fact]
    public async Task CatalogActionsRemainDistinctForSensitiveCapabilities()
    {
        var credentialId = Guid.NewGuid();
        await access.AddGrantAsync(admin.Id, user.Id, null, "credential.use", "credential", credentialId);
        Assert.True(await access.IsAllowedAsync(user.Id, "credential.use", "credential", credentialId));
        Assert.False(await access.IsAllowedAsync(user.Id, "credential.reveal", "credential", credentialId));
        await access.AddGrantAsync(admin.Id, user.Id, null, "deployment.deploy", "deployment", Deployment);
        Assert.True(await access.IsAllowedAsync(user.Id, "deployment.deploy", "deployment", Deployment));
        Assert.False(await access.IsAllowedAsync(user.Id, "terminal.open", "deployment", Deployment));
        Assert.False(await access.IsAllowedAsync(user.Id, "files.write", "deployment", Deployment));
        Assert.False(await access.IsAllowedAsync(user.Id, "host.execute", "agent", Deployment));
    }

    private async Task AssertAllMutationsDeniedAsync(Guid actorId, Guid groupId, Guid grantId)
    {
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.CreateGroupAsync(actorId, "Forbidden"));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RenameGroupAsync(actorId, groupId, "Forbidden"));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.DeleteGroupAsync(actorId, groupId));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddMemberAsync(actorId, groupId, user.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RemoveMemberAsync(actorId, groupId, user.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.AddGrantAsync(actorId, user.Id, null, "deployment.read", "deployment", Deployment));
        await Assert.ThrowsAsync<SecurityOperationException>(() => access.RemoveGrantAsync(actorId, grantId));
    }

    private Task<bool> AllowedAsync(Guid accountId) => access.IsAllowedAsync(accountId, "deployment.read", "deployment", Deployment);
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
