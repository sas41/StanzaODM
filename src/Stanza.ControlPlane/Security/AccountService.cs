using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Security;

public sealed record AccountSummary(Guid Id, string Username, AccountRole Role, bool Enabled, string SecurityStamp);

public sealed class SecurityOperationException(string message) : Exception(message);

public sealed class AccountService(StanzaDbContext db, TimeProvider timeProvider)
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 1024;
    private static readonly PasswordHasher<AccountEntity> Hasher = new();
    private static readonly AccountEntity DummyAccount = new();
    private static readonly string DummyHash = Hasher.HashPassword(DummyAccount, "constant-work-dummy-password");

    public Task<bool> NeedsSetupAsync(CancellationToken cancellationToken = default) =>
        NeedsSetupCoreAsync(cancellationToken);

    private async Task<bool> NeedsSetupCoreAsync(CancellationToken ct) => !await db.Accounts.AnyAsync(ct);

    public async Task<AccountSummary> BootstrapAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var name = ValidateUsername(username);
        ValidatePassword(password);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"SecurityGuard\" (\"Id\", \"Revision\") VALUES (1, 0) ON CONFLICT (\"Id\") DO NOTHING", cancellationToken);
        if (claimed != 1 || await db.Accounts.AnyAsync(cancellationToken))
            throw new SecurityOperationException("Initial setup has already been completed.");
        var account = NewAccount(name, password, AccountRole.SystemAdmin);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.Entry(account).State = EntityState.Detached;
        return Summary(account);
    }

    public async Task<AccountSummary?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(username?.Trim() ?? "");
        var account = normalized.Length <= 100
            ? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedUsername == normalized, cancellationToken)
            : null;
        var boundedPassword = password is { Length: <= MaximumPasswordLength } ? password : "invalid-password";
        var result = Hasher.VerifyHashedPassword(account ?? DummyAccount, account?.PasswordHash ?? DummyHash, boundedPassword);
        if (account is null || !account.Enabled || password is null || password.Length > MaximumPasswordLength
            || result == PasswordVerificationResult.Failed) return null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var hash = Hasher.HashPassword(account, password);
            await db.Accounts.Where(x => x.Id == account.Id && x.PasswordHash == account.PasswordHash)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.PasswordHash, hash), cancellationToken);
        }
        return Summary(account);
    }

    public async Task<AccountSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return account is null ? null : Summary(account);
    }

    public async Task<IReadOnlyList<AccountSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Accounts.AsNoTracking().OrderBy(x => x.Username)
            .Select(x => new AccountSummary(x.Id, x.Username, x.Role, x.Enabled, x.SecurityStamp)).ToListAsync(cancellationToken);

    public async Task<AccountSummary> CreateAsync(Guid actorId, string username, string password, AccountRole role,
        CancellationToken cancellationToken = default)
    {
        var name = ValidateUsername(username);
        ValidatePassword(password);
        ValidateRole(role);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(cancellationToken);
        var actor = await RequiredAsync(actorId, cancellationToken);
        AuthorizeManagement(actor, role);
        var normalized = Normalize(name);
        if (await db.Accounts.AnyAsync(x => x.NormalizedUsername == normalized, cancellationToken))
            throw new SecurityOperationException("That username is already in use.");
        var account = NewAccount(name, password, role);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.Entry(account).State = EntityState.Detached;
        return Summary(account);
    }

    public async Task ChangePasswordAsync(Guid id, string currentPassword, string newPassword,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(newPassword);
        await MutateAsync(id, async account =>
        {
            if (!account.Enabled || currentPassword is null || currentPassword.Length > MaximumPasswordLength
                || Hasher.VerifyHashedPassword(account, account.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
                throw new SecurityOperationException("The current password is incorrect or the account is unavailable.");
            account.PasswordHash = Hasher.HashPassword(account, newPassword);
            await Task.CompletedTask;
        }, cancellationToken);
    }

    public Task SetEnabledAsync(Guid actorId, Guid targetId, bool enabled, CancellationToken cancellationToken = default) =>
        ManageAsync(actorId, targetId, async target =>
        {
            if (!enabled) await ProtectLastSystemAdminAsync(target, cancellationToken);
            target.Enabled = enabled;
        }, cancellationToken);

    public Task ResetPasswordAsync(Guid actorId, Guid targetId, string password, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        return ManageAsync(actorId, targetId, target =>
        {
            target.PasswordHash = Hasher.HashPassword(target, password);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public Task SetRoleAsync(Guid actorId, Guid targetId, AccountRole role, CancellationToken cancellationToken = default)
    {
        ValidateRole(role);
        return ManageAsync(actorId, targetId, async target =>
        {
            var actor = await RequiredAsync(actorId, cancellationToken);
            if (actor.Role != AccountRole.SystemAdmin)
                throw new SecurityOperationException("Only a System Admin can change account roles.");
            if (role != AccountRole.SystemAdmin) await ProtectLastSystemAdminAsync(target, cancellationToken);
            if (role != AccountRole.User && target.Role == AccountRole.User)
            {
                // Privileged accounts do not participate in delegated access. Promotion must not
                // leave a privileged member behind for an Admin to manipulate indirectly.
                await db.GroupMemberships.Where(x => x.AccountId == target.Id).ExecuteDeleteAsync(cancellationToken);
                await db.Grants.Where(x => x.AccountId == target.Id).ExecuteDeleteAsync(cancellationToken);
            }
            target.Role = role;
        }, cancellationToken);
    }

    public async Task DeleteAsync(Guid actorId, Guid targetId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(cancellationToken);
        var actor = await RequiredAsync(actorId, cancellationToken);
        var target = await RequiredAsync(targetId, cancellationToken);
        AuthorizeManagement(actor, target.Role);
        await ProtectLastSystemAdminAsync(target, cancellationToken);
        if (actorId == targetId)
            throw new SecurityOperationException("You cannot delete your own account.");
        await db.Accounts.Where(x => x.Id == targetId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private Task ManageAsync(Guid actorId, Guid targetId, Func<AccountEntity, Task> mutation, CancellationToken ct) =>
        MutateAsync(targetId, async target =>
        {
            AuthorizeManagement(await RequiredAsync(actorId, ct), target.Role);
            await mutation(target);
        }, ct);

    private async Task MutateAsync(Guid id, Func<AccountEntity, Task> mutation, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);
        var account = await RequiredAsync(id, ct);
        await mutation(account);
        var stamp = Guid.NewGuid().ToString("N");
        await db.Accounts.Where(x => x.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.PasswordHash, account.PasswordHash)
            .SetProperty(x => x.Role, account.Role)
            .SetProperty(x => x.Enabled, account.Enabled)
            .SetProperty(x => x.SecurityStamp, stamp), ct);
        await transaction.CommitAsync(ct);
    }

    private Task LockAsync(CancellationToken ct) => SecurityWriteGuard.LockAsync(db, ct);

    private async Task ProtectLastSystemAdminAsync(AccountEntity target, CancellationToken ct)
    {
        if (target.Role == AccountRole.SystemAdmin && target.Enabled
            && !await db.Accounts.AnyAsync(x => x.Id != target.Id && x.Enabled && x.Role == AccountRole.SystemAdmin, ct))
            throw new SecurityOperationException("The final enabled System Admin cannot be disabled or demoted.");
    }

    private async Task<AccountEntity> RequiredAsync(Guid id, CancellationToken ct) =>
        await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new SecurityOperationException("The account is unavailable.");

    private static void AuthorizeManagement(AccountEntity actor, AccountRole targetRole)
    {
        if (!actor.Enabled || (actor.Role != AccountRole.SystemAdmin && (actor.Role != AccountRole.Admin || targetRole != AccountRole.User)))
            throw new SecurityOperationException("You do not have permission to manage this account.");
    }

    private AccountEntity NewAccount(string username, string password, AccountRole role)
    {
        var account = new AccountEntity
        {
            Id = Guid.NewGuid(), Username = username, NormalizedUsername = Normalize(username), Role = role,
            Enabled = true, SecurityStamp = Guid.NewGuid().ToString("N"), CreatedAt = timeProvider.GetUtcNow()
        };
        account.PasswordHash = Hasher.HashPassword(account, password);
        return account;
    }

    private static AccountSummary Summary(AccountEntity account) =>
        new(account.Id, account.Username, account.Role, account.Enabled, account.SecurityStamp);

    private static string Normalize(string username) => username.ToUpperInvariant();

    private static string ValidateUsername(string username)
    {
        var name = username?.Trim() ?? "";
        if (name.Length is < 1 or > 100 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-' and not '@'))
            throw new SecurityOperationException("Use a username of 1–100 letters, numbers, dots, underscores, hyphens, or @ signs.");
        return name;
    }

    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length is < MinimumPasswordLength or > MaximumPasswordLength || string.IsNullOrWhiteSpace(password))
            throw new SecurityOperationException($"Passwords must contain {MinimumPasswordLength}–{MaximumPasswordLength} characters and cannot be only whitespace.");
    }

    private static void ValidateRole(AccountRole role)
    {
        if (!Enum.IsDefined(role)) throw new SecurityOperationException("Select a valid account role.");
    }
}
