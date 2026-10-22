using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Audit;

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Resource { get; set; } = "";
    public string Result { get; set; } = "";
    public long At { get; set; }
}
public sealed class AuditConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents").HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(200);
        builder.Property(x => x.Resource).HasMaxLength(200);
        builder.Property(x => x.Result).HasMaxLength(100);
        builder.HasIndex(x => x.At);
    }
}
public sealed class AuditService(StanzaDbContext db, TimeProvider clock, AccessService access)
{
    public async Task WriteAsync(Guid? actor, string action, string resource, string result, CancellationToken ct = default)
    {
        var entry = new AuditEvent { Id = Guid.NewGuid(), ActorId = actor, Action = Limit(action, 200), Resource = Limit(resource, 200), Result = Limit(result, 100), At = clock.GetUtcNow().ToUnixTimeSeconds() };
        db.Set<AuditEvent>().Add(entry);
        try { await db.SaveChangesAsync(ct); }
        finally { db.Entry(entry).State = EntityState.Detached; }
    }
    public async Task<IReadOnlyList<AuditEvent>> ListAsync(Guid actor, string? search, CancellationToken ct = default)
    {
        if (!await access.IsAllowedAsync(actor, "audit.read", "instance", null, ct)) throw new SecurityOperationException("Audit read permission is required.");
        var query = db.Set<AuditEvent>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) { var term = Limit(search, 100); query = query.Where(x => x.Action.Contains(term) || x.Resource.Contains(term) || x.Result.Contains(term)); }
        return await query.OrderByDescending(x => x.At).Take(500).ToListAsync(ct);
    }
    private static string Limit(string value, int length) => new(value.Where(c => !char.IsControl(c)).Take(length).ToArray());
}
public static class AuditExtensions
{
    public static IApplicationBuilder UseStanzaAudit(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        await next();
        if (context.Request.Method is not ("POST" or "PUT" or "PATCH" or "DELETE") || context.Request.Path.StartsWithSegments("/api/agent-enrollment")) return;
        var actor = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
        if (actor is null && !await context.RequestServices.GetRequiredService<StanzaDbContext>().Accounts.AnyAsync(CancellationToken.None)) return;
        var controller = context.Request.RouteValues.GetValueOrDefault("controller")?.ToString() ?? "api";
        var action = context.Request.RouteValues.GetValueOrDefault("action")?.ToString() ?? "mutation";
        var resource = Guid.TryParse(context.Request.RouteValues.GetValueOrDefault("id")?.ToString(), out var resourceId) ? resourceId.ToString() : controller;
        try { await context.RequestServices.GetRequiredService<AuditService>().WriteAsync(actor, controller + "." + action, resource, context.Response.StatusCode.ToString(), CancellationToken.None); }
        catch (Exception) { context.RequestServices.GetRequiredService<ILogger<AuditService>>().LogError("Audit persistence failed for an HTTP mutation."); }
    });
}
