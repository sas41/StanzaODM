using Microsoft.EntityFrameworkCore;
using Stanza.Core.Security;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Security;

public sealed class AccountConcurrencyTests : IAsyncLifetime
{
    private const string Password = "a-secure-password-123";
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"stanza-security-{Guid.NewGuid():N}.db");
    private DbContextOptions<StanzaDbContext> Options => new DbContextOptionsBuilder<StanzaDbContext>()
        .UseSqlite($"Data Source={databasePath};Default Timeout=15;Pooling=False").Options;

    public async Task InitializeAsync()
    {
        await using var db = new StanzaDbContext(Options);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        File.Delete(databasePath);
        File.Delete(databasePath + "-wal");
        File.Delete(databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SimultaneousBootstrapClaimsYieldExactlyOneSystemAdmin()
    {
        var results = await RaceAsync(
            service => service.BootstrapAsync("first", Password),
            service => service.BootstrapAsync("second", Password));
        Assert.Single(results, result => result);
        await using var db = new StanzaDbContext(Options);
        var account = Assert.Single(await db.Accounts.ToListAsync());
        Assert.Equal(AccountRole.SystemAdmin, account.Role);
        Assert.True(account.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSelfDisablingOrDemotingCannotRemoveFinalUsableSystemAdmin(bool demote)
    {
        await using var db = new StanzaDbContext(Options);
        var service = new AccountService(db, TimeProvider.System);
        var first = await service.BootstrapAsync("first", Password);
        var second = await service.CreateAsync(first.Id, "second", Password, AccountRole.SystemAdmin);
        var results = await RaceAsync(
            s => demote ? s.SetRoleAsync(first.Id, first.Id, AccountRole.User) : s.SetEnabledAsync(first.Id, first.Id, false),
            s => demote ? s.SetRoleAsync(second.Id, second.Id, AccountRole.User) : s.SetEnabledAsync(second.Id, second.Id, false));
        Assert.Single(results, result => result);
        Assert.Single(await db.Accounts.AsNoTracking().Where(account => account.Enabled && account.Role == AccountRole.SystemAdmin).ToListAsync());
    }

    [Fact]
    public async Task ConcurrentNormalizedUsernameCreationYieldsOneSafeFailure()
    {
        await using var db = new StanzaDbContext(Options);
        var root = await new AccountService(db, TimeProvider.System).BootstrapAsync("root", Password);
        var results = await RaceAsync(
            service => service.CreateAsync(root.Id, "Alice", Password, AccountRole.User),
            service => service.CreateAsync(root.Id, " aLiCe ", Password, AccountRole.User));
        Assert.Single(results, result => result);
        Assert.Single(await db.Accounts.AsNoTracking().Where(account => account.NormalizedUsername == "ALICE").ToListAsync());
    }

    [Fact]
    public async Task PromotionRacingWithGroupMembershipNeverLeavesAPrivilegedGroupMember()
    {
        await using var db = new StanzaDbContext(Options);
        var accounts = new AccountService(db, TimeProvider.System);
        var access = new AccessService(db, TimeProvider.System);
        var root = await accounts.BootstrapAsync("root", Password);
        var admin = await accounts.CreateAsync(root.Id, "admin", Password, AccountRole.Admin);
        var user = await accounts.CreateAsync(root.Id, "user", Password, AccountRole.User);
        var group = await access.CreateGroupAsync(admin.Id, "Readers");
        await RaceSecurityAsync(
            (accountService, _) => accountService.SetRoleAsync(root.Id, user.Id, AccountRole.Admin),
            (_, accessService) => accessService.AddMemberAsync(admin.Id, group.Id, user.Id));
        Assert.Equal(AccountRole.Admin, (await accounts.FindAsync(user.Id))!.Role);
        Assert.Empty(await db.GroupMemberships.AsNoTracking().Where(x => x.AccountId == user.Id).ToListAsync());
    }

    [Fact]
    public async Task PromotionRacingWithAdminDirectGrantNeverLetsAdminAlterAPrivilegedAccount()
    {
        await using var db = new StanzaDbContext(Options);
        var accounts = new AccountService(db, TimeProvider.System);
        var root = await accounts.BootstrapAsync("root", Password);
        var admin = await accounts.CreateAsync(root.Id, "admin", Password, AccountRole.Admin);
        var user = await accounts.CreateAsync(root.Id, "user", Password, AccountRole.User);
        await RaceSecurityAsync(
            (accountService, _) => accountService.SetRoleAsync(root.Id, user.Id, AccountRole.SystemAdmin),
            (_, accessService) => accessService.AddGrantAsync(admin.Id, user.Id, null, "deployment.read", "deployment", null));
        Assert.Equal(AccountRole.SystemAdmin, (await accounts.FindAsync(user.Id))!.Role);
        Assert.Empty(await db.Grants.AsNoTracking().Where(x => x.AccountId == user.Id).ToListAsync());
    }

    [Fact]
    public async Task ConcurrentDuplicateGroupNamesAndGrantsYieldSafeBusinessFailures()
    {
        await using var db = new StanzaDbContext(Options);
        var accounts = new AccountService(db, TimeProvider.System);
        var root = await accounts.BootstrapAsync("root", Password);
        var user = await accounts.CreateAsync(root.Id, "user", Password, AccountRole.User);
        var groupResults = await RaceSecurityAsync(
            (_, accessService) => accessService.CreateGroupAsync(root.Id, "Readers"),
            (_, accessService) => accessService.CreateGroupAsync(root.Id, " readers "));
        Assert.Single(groupResults, success => success);
        Assert.Single(await db.Groups.AsNoTracking().ToListAsync());
        var grantResults = await RaceSecurityAsync(
            (_, accessService) => accessService.AddGrantAsync(root.Id, user.Id, null, "deployment.read", "deployment", null),
            (_, accessService) => accessService.AddGrantAsync(root.Id, user.Id, null, "deployment.read", "deployment", null));
        Assert.Single(grantResults, success => success);
        Assert.Single(await db.Grants.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ConcurrentSystemAdminDeletionAttemptsCannotRemoveBothAccounts()
    {
        await using var db = new StanzaDbContext(Options);
        var accounts = new AccountService(db, TimeProvider.System);
        var root = await accounts.BootstrapAsync("root", Password);
        var other = await accounts.CreateAsync(root.Id, "other", Password, AccountRole.SystemAdmin);
        var results = await RaceAsync(
            service => service.DeleteAsync(root.Id, other.Id),
            service => service.DeleteAsync(other.Id, root.Id));
        Assert.Single(results, success => success);
        Assert.Single(await db.Accounts.AsNoTracking().Where(x => x.Role == AccountRole.SystemAdmin && x.Enabled).ToListAsync());
    }

    private Task<bool[]> RaceAsync(Func<AccountService, Task> first, Func<AccountService, Task> second) =>
        RaceSecurityAsync((accounts, _) => first(accounts), (accounts, _) => second(accounts));

    private async Task<bool[]> RaceSecurityAsync(Func<AccountService, AccessService, Task> first,
        Func<AccountService, AccessService, Task> second)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> RunAsync(Func<AccountService, AccessService, Task> action)
        {
            await start.Task;
            await using var db = new StanzaDbContext(Options);
            try
            {
                await action(new AccountService(db, TimeProvider.System), new AccessService(db, TimeProvider.System));
                return true;
            }
            catch (SecurityOperationException)
            {
                return false;
            }
        }
        var task1 = Task.Run(() => RunAsync(first));
        var task2 = Task.Run(() => RunAsync(second));
        start.SetResult();
        return await Task.WhenAll(task1, task2);
    }
}
