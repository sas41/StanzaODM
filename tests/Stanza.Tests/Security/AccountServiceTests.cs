using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Security;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Security;

public sealed class AccountServiceTests : IAsyncLifetime
{
    private const string Password = "a-secure-password-123";
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StanzaDbContext db = null!;
    private AccountService service = null!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        service = new(db, new FixedClock());
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BootstrapCreatesExactlyOneEnabledSystemAdminWithHashedPasswordAndClockTimestamp()
    {
        Assert.True(await service.NeedsSetupAsync());
        var account = await service.BootstrapAsync("  Root  ", Password);
        Assert.False(await service.NeedsSetupAsync());
        Assert.Equal("Root", account.Username);
        Assert.Equal(AccountRole.SystemAdmin, account.Role);
        Assert.True(account.Enabled);
        Assert.NotEmpty(account.SecurityStamp);
        var stored = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.NotEqual(Password, stored.PasswordHash);
        Assert.Equal("ROOT", stored.NormalizedUsername);
        Assert.Equal(FixedClock.Now, stored.CreatedAt);
        Assert.DoesNotContain("Password", System.Text.Json.JsonSerializer.Serialize(account));
        Assert.DoesNotContain(stored.PasswordHash, System.Text.Json.JsonSerializer.Serialize(stored));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.BootstrapAsync("other", Password));
        Assert.Single(await service.ListAsync());
    }

    [Fact]
    public async Task FailedBootstrapValidationDoesNotClaimSetup()
    {
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.BootstrapAsync("root", "short"));
        Assert.True(await service.NeedsSetupAsync());
        Assert.NotNull(await service.BootstrapAsync("root", Password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    [InlineData("ro\not")]
    [InlineData("<script>")]
    [InlineData("røot")]
    public async Task InvalidUsernamesAreRejected(string username)
    {
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.BootstrapAsync(username, Password));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(1025)]
    public async Task PasswordLengthBoundsAreEnforced(int length)
    {
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.BootstrapAsync("root", new string('x', length)));
    }

    [Fact]
    public async Task UsernameLengthBoundsAreEnforced()
    {
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.BootstrapAsync(new string('x', 101), Password));
        var root = await service.BootstrapAsync(new string('x', 100), Password);
        Assert.Equal(100, root.Username.Length);
    }

    [Fact]
    public async Task BoundaryPasswordsAreSupportedAndWhitespaceOnlyPasswordRejected()
    {
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.BootstrapAsync("root", new string(' ', 12)));
        var root = await service.BootstrapAsync("root", new string('x', 12));
        await service.ChangePasswordAsync(root.Id, new string('x', 12), new string('y', 1024));
        Assert.NotNull(await service.AuthenticateAsync("root", new string('y', 1024)));
    }

    [Fact]
    public async Task AuthenticationUsesNormalizedUsernameAndRejectsUnknownIncorrectOrOversizeCredentials()
    {
        var root = await service.BootstrapAsync("Root", Password);
        Assert.Equal(root.Id, (await service.AuthenticateAsync(" root ", Password))?.Id);
        Assert.Null(await service.AuthenticateAsync("unknown", Password));
        Assert.Null(await service.AuthenticateAsync("Root", "incorrect-password"));
        Assert.Null(await service.AuthenticateAsync("Root", new string('x', 1025)));
        Assert.Null(await service.AuthenticateAsync(new string('x', 101), Password));
    }

    [Fact]
    public async Task UsernamesAreUniqueAfterTrimmingAndCaseNormalization()
    {
        var root = await service.BootstrapAsync("root", Password);
        await service.CreateAsync(root.Id, "Alice", Password, AccountRole.User);
        var error = await Assert.ThrowsAsync<SecurityOperationException>(() =>
            service.CreateAsync(root.Id, "  aLiCe  ", Password, AccountRole.User));
        Assert.Equal("That username is already in use.", error.Message);
        Assert.Equal(2, (await service.ListAsync()).Count);
    }

    [Fact]
    public async Task SelfServicePasswordChangeRequiresCurrentPasswordAndRotatesStamp()
    {
        var root = await service.BootstrapAsync("root", Password);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.ChangePasswordAsync(root.Id, "wrong", "new-secure-password"));
        Assert.Equal(root.SecurityStamp, (await service.FindAsync(root.Id))?.SecurityStamp);
        await service.ChangePasswordAsync(root.Id, Password, "new-secure-password");
        Assert.NotEqual(root.SecurityStamp, (await service.FindAsync(root.Id))?.SecurityStamp);
        Assert.Null(await service.AuthenticateAsync("root", Password));
        Assert.NotNull(await service.AuthenticateAsync("root", "new-secure-password"));
    }

    [Theory]
    [InlineData(AccountRole.User)]
    [InlineData(AccountRole.Admin)]
    [InlineData(AccountRole.SystemAdmin)]
    public async Task EveryEnabledRoleCanChangeItsOwnPassword(AccountRole role)
    {
        var root = await service.BootstrapAsync("root", Password);
        var account = await service.CreateAsync(root.Id, "account", Password, role);
        await service.ChangePasswordAsync(account.Id, Password, "new-secure-password");
        Assert.NotNull(await service.AuthenticateAsync("account", "new-secure-password"));
    }

    [Theory]
    [InlineData(AccountRole.Admin)]
    [InlineData(AccountRole.SystemAdmin)]
    public async Task AdminCannotCreateOrManagePrivilegedAccounts(AccountRole protectedRole)
    {
        var root = await service.BootstrapAsync("root", Password);
        var admin = await service.CreateAsync(root.Id, "admin", Password, AccountRole.Admin);
        var target = await service.CreateAsync(root.Id, "target", Password, protectedRole);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.CreateAsync(admin.Id, "forbidden", Password, protectedRole));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetEnabledAsync(admin.Id, target.Id, false));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.ResetPasswordAsync(admin.Id, target.Id, "new-secure-password"));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetRoleAsync(admin.Id, target.Id, AccountRole.User));
        Assert.Equal(target, await service.FindAsync(target.Id));
    }

    [Fact]
    public async Task AdminCanManageOrdinaryUsersButCannotPromoteAnyoneOrManageItself()
    {
        var root = await service.BootstrapAsync("root", Password);
        var admin = await service.CreateAsync(root.Id, "admin", Password, AccountRole.Admin);
        var user = await service.CreateAsync(admin.Id, "user", Password, AccountRole.User);
        await service.ResetPasswordAsync(admin.Id, user.Id, "new-secure-password");
        var reset = (await service.FindAsync(user.Id))!;
        Assert.NotEqual(user.SecurityStamp, reset.SecurityStamp);
        Assert.NotNull(await service.AuthenticateAsync("user", "new-secure-password"));
        await service.SetEnabledAsync(admin.Id, user.Id, false);
        Assert.Null(await service.AuthenticateAsync("user", "new-secure-password"));
        Assert.NotEqual(reset.SecurityStamp, (await service.FindAsync(user.Id))?.SecurityStamp);
        await service.SetEnabledAsync(admin.Id, user.Id, true);
        Assert.NotNull(await service.AuthenticateAsync("user", "new-secure-password"));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetRoleAsync(admin.Id, user.Id, AccountRole.Admin));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetRoleAsync(admin.Id, user.Id, AccountRole.User));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetRoleAsync(admin.Id, admin.Id, AccountRole.SystemAdmin));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetEnabledAsync(admin.Id, admin.Id, false));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.ResetPasswordAsync(admin.Id, admin.Id, "new-secure-password"));
    }

    [Fact]
    public async Task UserCannotManageAccountsIncludingItself()
    {
        var root = await service.BootstrapAsync("root", Password);
        var user = await service.CreateAsync(root.Id, "user", Password, AccountRole.User);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.CreateAsync(user.Id, "other", Password, AccountRole.User));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetEnabledAsync(user.Id, user.Id, false));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.ResetPasswordAsync(user.Id, user.Id, "new-secure-password"));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetRoleAsync(user.Id, user.Id, AccountRole.SystemAdmin));
    }

    [Fact]
    public async Task DisabledAccountsCannotAuthenticateChangePasswordOrManageOthers()
    {
        var root = await service.BootstrapAsync("root", Password);
        var admin = await service.CreateAsync(root.Id, "admin", Password, AccountRole.Admin);
        await service.SetEnabledAsync(root.Id, admin.Id, false);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.ChangePasswordAsync(admin.Id, Password, "new-secure-password"));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.CreateAsync(admin.Id, "other", Password, AccountRole.User));
        Assert.Null(await service.AuthenticateAsync("admin", Password));
    }

    [Fact]
    public async Task FinalUsableSystemAdminCannotBeDisabledOrDemotedEvenWhenAnotherIsDisabled()
    {
        var root = await service.BootstrapAsync("root", Password);
        var other = await service.CreateAsync(root.Id, "other", Password, AccountRole.SystemAdmin);
        await service.SetEnabledAsync(root.Id, other.Id, false);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetEnabledAsync(root.Id, root.Id, false));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetRoleAsync(root.Id, root.Id, AccountRole.Admin));
        Assert.Equal(root, await service.FindAsync(root.Id));
        await service.SetRoleAsync(root.Id, other.Id, AccountRole.User);
        Assert.Equal(AccountRole.User, (await service.FindAsync(other.Id))?.Role);
    }

    [Fact]
    public async Task SystemAdminCanDemoteOrDisableAnotherAndRoleChangesRotateStamp()
    {
        var root = await service.BootstrapAsync("root", Password);
        var other = await service.CreateAsync(root.Id, "other", Password, AccountRole.SystemAdmin);
        await service.SetRoleAsync(root.Id, root.Id, AccountRole.User);
        Assert.NotEqual(root.SecurityStamp, (await service.FindAsync(root.Id))?.SecurityStamp);
        await service.SetEnabledAsync(other.Id, root.Id, false);
        Assert.False((await service.FindAsync(root.Id))!.Enabled);
        await service.ResetPasswordAsync(other.Id, other.Id, "new-secure-password");
        Assert.NotNull(await service.AuthenticateAsync("other", "new-secure-password"));
    }

    [Fact]
    public async Task InvalidRolesAndMissingAccountsProduceSafeFailures()
    {
        var root = await service.BootstrapAsync("root", Password);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.CreateAsync(root.Id, "invalid", Password, (AccountRole)99));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetRoleAsync(root.Id, root.Id, (AccountRole)99));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.ResetPasswordAsync(root.Id, Guid.NewGuid(), Password));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.SetEnabledAsync(Guid.NewGuid(), root.Id, false));
        Assert.Null(await service.FindAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ServicesOnSeparateContextsObservePersistenceAndRevocation()
    {
        var root = await service.BootstrapAsync("root", Password);
        var user = await service.CreateAsync(root.Id, "user", Password, AccountRole.User);
        await using var secondDb = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(connection).Options);
        var secondService = new AccountService(secondDb, TimeProvider.System);
        Assert.Equal(user, await secondService.AuthenticateAsync("user", Password));
        await service.SetEnabledAsync(root.Id, user.Id, false);
        Assert.Null(await secondService.AuthenticateAsync("user", Password));
        Assert.NotEqual(user.SecurityStamp, (await secondService.FindAsync(user.Id))?.SecurityStamp);
        await Assert.ThrowsAsync<SecurityOperationException>(() => secondService.BootstrapAsync("another", Password));
    }

    [Fact]
    public async Task MembershipAndGrantConstraintsAreActuallyEnforcedByRelationalDatabase()
    {
        var root = await service.BootstrapAsync("root", Password);
        var group = new GroupEntity { Id = Guid.NewGuid(), Name = "Operators" };
        db.Groups.Add(group);
        db.GroupMemberships.Add(new() { AccountId = root.Id, GroupId = group.Id });
        db.Grants.Add(new() { Id = Guid.NewGuid(), GroupId = group.Id, Action = "deployment.read", ResourceType = "deployment" });
        await db.SaveChangesAsync();
        Assert.Single(await db.GroupMemberships.ToListAsync());
        Assert.Single(await db.Grants.ToListAsync());
        db.Grants.Add(new() { Id = Guid.NewGuid(), AccountId = root.Id, GroupId = group.Id, Action = "invalid", ResourceType = "instance" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AdminCanDeleteOrdinaryUserAndDatabaseCascadesDirectGrantsAndMemberships()
    {
        var root = await service.BootstrapAsync("root", Password);
        var admin = await service.CreateAsync(root.Id, "admin", Password, AccountRole.Admin);
        var user = await service.CreateAsync(admin.Id, "user", Password, AccountRole.User);
        var access = new AccessService(db, TimeProvider.System);
        var group = await access.CreateGroupAsync(admin.Id, "Readers");
        await access.AddMemberAsync(admin.Id, group.Id, user.Id);
        await access.AddGrantAsync(admin.Id, user.Id, null, "deployment.read", "deployment", null);
        await service.DeleteAsync(admin.Id, user.Id);
        Assert.Null(await service.FindAsync(user.Id));
        Assert.Null(await service.AuthenticateAsync("user", Password));
        Assert.Empty(await db.GroupMemberships.ToListAsync());
        Assert.Empty(await db.Grants.ToListAsync());
        Assert.Equal(group.Id, Assert.Single(await access.ListGroupsAsync()).Id);
    }

    [Theory]
    [InlineData(AccountRole.Admin)]
    [InlineData(AccountRole.SystemAdmin)]
    public async Task AdminCannotDeletePrivilegedAccounts(AccountRole targetRole)
    {
        var root = await service.BootstrapAsync("root", Password);
        var admin = await service.CreateAsync(root.Id, "admin", Password, AccountRole.Admin);
        var target = await service.CreateAsync(root.Id, "target", Password, targetRole);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.DeleteAsync(admin.Id, target.Id));
        Assert.Equal(target, await service.FindAsync(target.Id));
        await service.DeleteAsync(root.Id, target.Id);
        Assert.Null(await service.FindAsync(target.Id));
    }

    [Fact]
    public async Task SelfDeletionAndFinalSystemAdminDeletionArePrevented()
    {
        var root = await service.BootstrapAsync("root", Password);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.DeleteAsync(root.Id, root.Id));
        var other = await service.CreateAsync(root.Id, "other", Password, AccountRole.SystemAdmin);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.DeleteAsync(root.Id, root.Id));
        await service.SetRoleAsync(other.Id, root.Id, AccountRole.Admin);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.DeleteAsync(root.Id, other.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.DeleteAsync(other.Id, other.Id));
        Assert.Equal(2, (await service.ListAsync()).Count);
    }

    [Fact]
    public async Task UsersDisabledAdminsAndMissingActorsCannotDeleteAccounts()
    {
        var root = await service.BootstrapAsync("root", Password);
        var admin = await service.CreateAsync(root.Id, "admin", Password, AccountRole.Admin);
        var user = await service.CreateAsync(root.Id, "user", Password, AccountRole.User);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.DeleteAsync(user.Id, user.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.DeleteAsync(user.Id, admin.Id));
        await service.SetEnabledAsync(root.Id, admin.Id, false);
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.DeleteAsync(admin.Id, user.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.DeleteAsync(Guid.NewGuid(), user.Id));
        await Assert.ThrowsAsync<SecurityOperationException>(() => service.DeleteAsync(root.Id, Guid.NewGuid()));
        Assert.Equal(user, await service.FindAsync(user.Id));
    }

    private sealed class FixedClock : TimeProvider
    {
        public static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
