using System.Security.Cryptography;
using System.Text;
using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Contracts;
using Stanza.Core.Security;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Agents;

public sealed record AgentSummary(Guid Id, string Identity, string Hostname, string Status, long RegisteredAt, long LastSeenAt, long Version);
public sealed record AgentEnvironment(string Text, long Version)
{
    public override string ToString() => $"AgentEnvironment {{ Text = [REDACTED], Version = {Version} }}";
}
public sealed record IssuedEnrollmentToken(string Token, long ExpiresAt)
{
    public override string ToString() => "IssuedEnrollmentToken { [REDACTED] }";
}
public sealed class AgentOperationException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class AgentAdministrationService(StanzaDbContext store, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("Stanza.AgentEnvironment.v1");

    public async Task<IReadOnlyList<AgentSummary>> ListAsync(Guid actorId, CancellationToken token = default)
    {
        var rows = await store.Set<AgentEntity>().AsNoTracking().OrderBy(x => x.Hostname)
            .Select(x => new AgentSummary(x.Id, x.Identity, x.Hostname, x.Status, x.RegisteredAt, x.LastSeenAt, x.Version)).ToListAsync(token);
        var result = new List<AgentSummary>();
        foreach (var row in rows) if (await AllowedAsync(actorId, "agent.read", row.Id, token)) result.Add(row);
        if (result.Count == 0 && !await AllowedAsync(actorId, "agent.read", null, token)) throw new AgentOperationException("Agent read access is required.", 403);
        return result;
    }

    public async Task<IssuedEnrollmentToken> IssueTokenAsync(Guid actorId, int lifetimeMinutes, CancellationToken token = default)
    {
        if (lifetimeMinutes is < 1 or > 1440) throw new AgentOperationException("Enrollment token lifetime must be between 1 minute and 24 hours.");
        return await WriteAsync(async added =>
        {
            await RequireAsync(actorId, "agent.manage", null, token);
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var expires = clock.GetUtcNow().AddMinutes(lifetimeMinutes).ToUnixTimeSeconds();
            Add(new EnrollmentTokenEntity { Id = Guid.NewGuid(), Hash = Hash(raw), ExpiresAt = expires }, added);
            return new IssuedEnrollmentToken(raw, expires);
        }, token);
    }

    public async Task<AgentEnrollmentResponse> RegisterAsync(AgentEnrollmentRequest? request, CancellationToken token = default)
    {
        if (request is null) throw new AgentOperationException("Invalid enrollment proof.", 401);
        if (request.ProtocolVersion != AgentEnrollmentProtocol.Version)
            throw new AgentOperationException("Unsupported Agent protocol version.", 426);
        if (request.Token is not { Length: >= 32 } || !AgentEnrollmentProtocol.VerifyProof(request, clock.GetUtcNow()))
            throw new AgentOperationException("Invalid enrollment proof.", 401);
        return await WriteAsync(async added =>
        {
            // Recheck freshness after acquiring the transactional guard: waiting for a
            // concurrent administration request must not extend a proof/token lifetime.
            var now = clock.GetUtcNow();
            var nowSeconds = now.ToUnixTimeSeconds();
            if (!AgentEnrollmentProtocol.VerifyProof(request, now))
                throw new AgentOperationException("Invalid enrollment proof.", 401);
            await store.Set<AgentNonceEntity>().Where(x => x.ExpiresAt < nowSeconds).ExecuteDeleteAsync(token);
            var hash = Hash(request.Token);
            var credential = await store.Set<EnrollmentTokenEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Hash == hash, token);
            if (credential is null || credential.Revoked || ((credential.BoundIdentity is null || credential.FirstClaimPending) && credential.ExpiresAt <= nowSeconds))
                throw new AgentOperationException("Enrollment credential is unavailable.", 401);
            if (credential.BoundIdentity is not null && credential.BoundIdentity != request.AgentId)
                throw new AgentOperationException("Enrollment credential is bound to another identity.", 403);
            var agent = await store.Set<AgentEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Identity == request.AgentId, token);
            if (credential.BoundIdentity is null)
            {
                if (agent is not null)
                    throw new AgentOperationException("This identity is already registered. Use its bound credential or request administrator assistance.", 403);
                agent = new AgentEntity
                {
                    Id = Guid.NewGuid(), Identity = request.AgentId, PublicKey = request.PublicKey,
                    Hostname = request.Hostname, Status = AgentEnrollmentProtocol.Pending,
                    RegisteredAt = nowSeconds, LastSeenAt = nowSeconds
                };
                agent.ProtectedEnvironment = ProtectEnvironment(agent.Id, "");
                Add(agent, added);
                await store.Set<EnrollmentTokenEntity>().Where(x => x.Id == credential.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.BoundIdentity, request.AgentId), token);
            }
            else
            {
                if (agent is null || agent.PublicKey != request.PublicKey)
                    throw new AgentOperationException("The registered identity is unavailable.", 403);
                await store.Set<AgentEntity>().Where(x => x.Id == agent.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.LastSeenAt, nowSeconds).SetProperty(x => x.Hostname, request.Hostname), token);
                if (credential.FirstClaimPending) await store.Set<EnrollmentTokenEntity>().Where(x => x.Id == credential.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.FirstClaimPending, false), token);
            }
            if (await store.Set<AgentNonceEntity>().AnyAsync(x => x.Identity == request.AgentId && x.Nonce == request.Nonce, token))
                throw new AgentOperationException("Enrollment proof has already been used.", 409);
            // A proof accepted five minutes in the future can remain fresh for ten
            // minutes after acceptance. Retain its nonce beyond that entire window.
            Add(new AgentNonceEntity { Identity = request.AgentId, Nonce = request.Nonce, ExpiresAt = now.AddMinutes(11).ToUnixTimeSeconds() }, added);
            return new AgentEnrollmentResponse(AgentEnrollmentProtocol.Version, agent.Status);
        }, token);
    }

    public async Task SetStatusAsync(Guid actorId, Guid id, string status, CancellationToken token = default)
    {
        if (status is not (AgentEnrollmentProtocol.Approved or AgentEnrollmentProtocol.Rejected or AgentEnrollmentProtocol.Revoked))
            throw new AgentOperationException("Invalid registration decision.");
        await WriteAsync(async _ =>
        {
            await RequireAsync(actorId, "agent.manage", id, token);
            var agent = await RequiredAsync(id, token);
            if (agent.Status != AgentEnrollmentProtocol.Pending && status != AgentEnrollmentProtocol.Revoked)
                throw new AgentOperationException("Only a pending registration can be approved or rejected.");
            var version = checked(agent.Version + 1);
            await store.Set<AgentEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, status).SetProperty(x => x.Version, version), token);
            return true;
        }, token);
    }

    public async Task<AgentEnvironment> ReadEnvironmentAsync(Guid actorId, Guid id, CancellationToken token = default)
    {
        await RequireAsync(actorId, "environment.reveal", id, token);
        var agent = await RequiredAsync(id, token);
        try { return new(EnvironmentProtector(id).Unprotect(agent.ProtectedEnvironment), agent.Version); }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new AgentOperationException("Agent environment protection is unavailable. Check the persistent protection keys.", 503);
        }
    }

    public async Task SaveEnvironmentAsync(Guid actorId, Guid id, AgentEnvironment? environment, CancellationToken token = default)
    {
        if (!ValidEnvironment(environment))
            throw new AgentOperationException("Environment text must not contain null bytes and must be no larger than 1 MiB.");
        await WriteAsync(async _ =>
        {
            await RequireAsync(actorId, "environment.write", id, token);
            var agent = await RequiredAsync(id, token);
            if (agent.Version != environment!.Version) throw new AgentOperationException("The Agent configuration changed. Reload before saving.", 409);
            var protectedText = ProtectEnvironment(id, environment.Text);
            var version = checked(agent.Version + 1);
            await store.Set<AgentEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.ProtectedEnvironment, protectedText).SetProperty(x => x.Version, version), token);
            return true;
        }, token);
    }

    private Task<bool> AllowedAsync(Guid actorId, string action, Guid? id, CancellationToken token) =>
        new AccessService(store, clock).IsAllowedAsync(actorId, action, "agent", id, token);

    private async Task RequireAsync(Guid actorId, string action, Guid? id, CancellationToken token)
    {
        if (!await AllowedAsync(actorId, action, id, token)) throw new AgentOperationException("Agent permission is required: " + action, 403);
    }

    public Task<IssuedEnrollmentToken> RotateCredentialAsync(Guid actorId, Guid id, CancellationToken token = default) => WriteAsync(async added =>
    {
        await RequireAsync(actorId, "agent.manage", id, token);
        var agent = await RequiredAsync(id, token);
        await store.Set<EnrollmentTokenEntity>().Where(x => x.BoundIdentity == agent.Identity).ExecuteUpdateAsync(update => update.SetProperty(x => x.Revoked, true).SetProperty(x => x.BoundIdentity, (string?)null), token);
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds();
        Add(new EnrollmentTokenEntity { Id = Guid.NewGuid(), Hash = Hash(raw), ExpiresAt = expires, BoundIdentity = agent.Identity, FirstClaimPending = true }, added);
        await store.Set<AgentEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "pending").SetProperty(x => x.Version, x => x.Version + 1), token);
        return new IssuedEnrollmentToken(raw, expires);
    }, token);

    private Task<AgentEntity> RequiredAsync(Guid id, CancellationToken token) =>
        RequiredCoreAsync(id, token);

    private async Task<AgentEntity> RequiredCoreAsync(Guid id, CancellationToken token) =>
        await store.Set<AgentEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)
        ?? throw new AgentOperationException("Agent not found.", 404);

    private async Task LockAsync(CancellationToken token)
    {
        if (await store.Database.ExecuteSqlRawAsync("UPDATE \"SecurityGuard\" SET \"Revision\" = \"Revision\" + 1 WHERE \"Id\" = 1", token) != 1)
            throw new AgentOperationException("Complete initial setup before enrolling Agents.", 403);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private IDataProtector EnvironmentProtector(Guid id) => protector.CreateProtector(id.ToString("N"));

    private string ProtectEnvironment(Guid id, string text)
    {
        try { return EnvironmentProtector(id).Protect(text); }
        catch (CryptographicException) { throw new AgentOperationException("Agent environment protection is unavailable. Check the persistent protection keys.", 503); }
    }

    private static bool ValidEnvironment(AgentEnvironment? environment)
    {
        if (environment is null || environment.Version < 0 || environment.Text is null || environment.Text.Contains('\0')) return false;
        try { return new UTF8Encoding(false, true).GetByteCount(environment.Text) <= 1024 * 1024; }
        catch (EncoderFallbackException) { return false; }
    }

    private void Add<TEntity>(TEntity entity, List<object> added) where TEntity : class
    {
        added.Add(entity);
        store.Set<TEntity>().Add(entity);
    }

    private async Task<TResult> WriteAsync<TResult>(Func<List<object>, Task<TResult>> action, CancellationToken token)
    {
        var added = new List<object>();
        try
        {
            await using var transaction = await store.Database.BeginTransactionAsync(token);
            await LockAsync(token);
            var result = await action(added);
            await store.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return result;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            throw new AgentOperationException("Agent state storage is unavailable. Retry the operation.", 503);
        }
        finally
        {
            // Rollback does not reset EF tracking. Never let a failed claim/nonce be
            // accidentally persisted by a later operation on the same scoped service.
            foreach (var entity in added) store.Entry(entity).State = EntityState.Detached;
        }
    }
}
