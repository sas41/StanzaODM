using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Automation;

public static partial class WebhookSecurity
{
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static bool Verify(string secret, ReadOnlySpan<byte> body, string signature)
    {
        if (!signature.StartsWith("sha256=", StringComparison.Ordinal) || signature.Length != 71) return false;
        byte[] supplied;
        try { supplied = Convert.FromHexString(signature.AsSpan(7)); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    [GeneratedRegex("^[A-Za-z0-9_.:/-]{1,200}$", RegexOptions.CultureInvariant)] private static partial Regex TokenPattern();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*){0,7}$", RegexOptions.CultureInvariant)] private static partial Regex PathPattern();
    public static bool Token(string text) => TokenPattern().IsMatch(text);
    public static IReadOnlyList<string> Filters(string text) => text.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static void ValidateConfiguration(WebhookDefinition hook)
    {
        if (hook.Provider is not ("github" or "generic") || hook.Events is null || hook.Refs is null || hook.ParameterMapping is null
            || hook.Events.Length > 2048 || hook.Refs.Length > 4096 || hook.ParameterMapping.Length > 8192
            || Filters(hook.Events).Count == 0 || Filters(hook.Events).Any(x => !Token(x)) || Filters(hook.Refs).Any(x => !Token(x)))
            throw new AutomationException("Select GitHub/generic, bounded exact event/ref filters, and a JSON parameter mapping.");
    }

    public static Dictionary<string, string> ParseMapping(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<Dictionary<string, string>>(json, AutomationRules.Json) ?? throw new AutomationException("Invalid parameter mapping.");
            if (result.Count > 50 || result.Any(x => x.Value is null || !PathPattern().IsMatch(x.Value)))
                throw new AutomationException("Mapping values must be bounded dot-separated JSON object paths.");
            return result;
        }
        catch (JsonException) { throw new AutomationException("Invalid parameter mapping JSON."); }
    }
    public static void ValidateMapping(string json, ActionPlan plan)
    {
        var mapping = ParseMapping(json);
        if (mapping.Keys.Any(k => (plan.Parameters ?? []).All(p => p.Name != k))) throw new AutomationException("Mapping references an undeclared action parameter.");
    }

    public static IReadOnlyDictionary<string, string> Map(string json, JsonElement payload)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in ParseMapping(json))
        {
            var value = payload;
            foreach (var part in entry.Value.Split('.'))
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) throw new AutomationException("A mapped payload field is missing.");
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                throw new AutomationException("Mapped payload fields must be scalar values.");
            values.Add(entry.Key, value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
        }
        return values;
    }
}

public sealed record WebhookReceipt(int Status, string Code, Guid? RunId = null);

public sealed class WebhookReceiver(StanzaDbContext db, AutomationService automation, AutomationAuthorization authorization)
{
    public async Task<WebhookReceipt> ReceiveAsync(Guid id, string signature, string deliveryId, string eventName, byte[] body, CancellationToken ct = default)
    {
        var limits = await automation.LimitsAsync(ct);
        if (body.Length > limits.MaximumWebhookBytes) return new(413, "payload-too-large");
        if (!WebhookSecurity.Token(deliveryId) || !WebhookSecurity.Token(eventName) || eventName.Length > 100) return new(400, "invalid-delivery-headers");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var hook = await db.Set<WebhookDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (hook is null || !hook.Enabled || hook.ProtectedSecret.Length == 0) return new(404, "unavailable");
        if (!WebhookSecurity.Verify(automation.Unprotect<string>(hook.ProtectedSecret), body, signature)) return new(401, "invalid-signature");
        var prior = await db.Set<WebhookDelivery>().AsNoTracking().SingleOrDefaultAsync(x => x.WebhookId == id && x.DeliveryId == deliveryId, ct);
        if (prior is not null) return new(200, "duplicate", prior.RunId);
        var window = automation.Now / 60;
        var count = hook.RateWindow == window ? hook.RateCount + 1 : 1;
        if (count > limits.DeliveriesPerMinute) return new(429, "rate-limited");
        // Revision CAS serializes rate-limit, secret rotation/revocation, and deliveries across workers.
        if (await db.Set<WebhookDefinition>().Where(x => x.Id == id && x.Revision == hook.Revision && x.Enabled)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.RateWindow, window).SetProperty(x => x.RateCount, count)
                    .SetProperty(x => x.Revision, x => x.Revision + 1), ct) != 1) return new(409, "retry-delivery");
        var delivery = new WebhookDelivery { Id = Guid.NewGuid(), WebhookId = id, DeliveryId = deliveryId, Event = eventName,
            ReceivedAt = automation.Now, State = "accepted" };
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 24 });
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object) throw new AutomationException("Payload must be a JSON object.");
            var branch = payload.TryGetProperty("ref", out var reference) && reference.ValueKind == JsonValueKind.String ? reference.GetString() : null;
            if (!WebhookSecurity.Filters(hook.Events).Contains(eventName, StringComparer.Ordinal)
                || (WebhookSecurity.Filters(hook.Refs).Count > 0 && !WebhookSecurity.Filters(hook.Refs).Contains(branch, StringComparer.Ordinal)))
                delivery.State = "filtered";
            else
            {
                await authorization.RequireAsync(hook.OwnerId, "automation.manage", "deployment", hook.DeploymentId, ct);
                await automation.ValidateTriggerAsync(hook.OwnerId, hook.ActionId, hook.DeploymentId, hook.Operation, ct);
                var parameters = WebhookSecurity.Map(hook.ParameterMapping, payload);
                var deliveryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deliveryId)));
                var run = await automation.QueueAsync(hook.OwnerId, hook.DeploymentId, hook.ActionId, id, "webhook", $"webhook:{id:N}:{deliveryKey}",
                    hook.Operation, parameters, ct: ct);
                delivery.RunId = run.Id;
            }
        }
        catch (AutomationAccessException) { delivery.State = "denied"; }
        catch (Exception error) when (error is JsonException or AutomationException) { delivery.State = "invalid-payload-or-target"; }
        db.Add(delivery);
        await db.SaveChangesAsync(ct); db.Entry(delivery).State = EntityState.Detached;
        await transaction.CommitAsync(ct);
        return new(delivery.State == "accepted" ? 202 : delivery.State == "denied" ? 403 : delivery.State == "filtered" ? 200 : 400, delivery.State, delivery.RunId);
    }
}
