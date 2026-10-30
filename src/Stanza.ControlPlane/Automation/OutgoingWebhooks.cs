using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;

namespace Stanza.ControlPlane.Automation;

public sealed record OutgoingChannel(Guid Id, string Name, string Provider, string Url, string Template, bool Enabled,
    bool Started, bool Succeeded, bool Failed, long CreatedAt)
{
    public override string ToString() => $"OutgoingChannel {{ Id = {Id}, Configuration = [REDACTED] }}";
}
public sealed record OutgoingConfiguration(long Revision, IReadOnlyList<OutgoingChannel> Channels);
public sealed record OutgoingEditor(Guid? Id = null, long Revision = 0, string Name = "", string Provider = "discord",
    string? Url = null, string? Template = null, bool Enabled = true, bool Started = false, bool Succeeded = true, bool Failed = true);
public sealed record OutgoingPage(OutgoingConfiguration Configuration, IReadOnlyList<OutgoingDelivery> Deliveries);

public sealed class OutgoingDelivery
{
    public Guid Id { get; set; }
    public Guid ChannelId { get; set; }
    public Guid RunId { get; set; }
    public string Event { get; set; } = "";
    public string State { get; set; } = "pending";
    public int Attempts { get; set; }
    public long NextAttemptAt { get; set; }
    public string Result { get; set; } = "";
    [JsonIgnore] public string ProtectedBody { get; set; } = "";
}
public sealed class OutgoingDeliveryConfiguration : IEntityTypeConfiguration<OutgoingDelivery>
{
    public void Configure(EntityTypeBuilder<OutgoingDelivery> b)
    {
        b.ToTable("OutgoingDeliveries").HasKey(x => x.Id);
        b.HasIndex(x => new { x.ChannelId, x.RunId, x.Event }).IsUnique();
        b.HasIndex(x => new { x.State, x.NextAttemptAt });
    }
}

public static class OutgoingTemplates
{
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>
    {
        ["discord"] = """{"content":"{message}","allowed_mentions":{"parse":[]}}""",
        ["teams"] = """{"type":"message","attachments":[{"contentType":"application/vnd.microsoft.card.adaptive","content":{"type":"AdaptiveCard","version":"1.2","body":[{"type":"TextBlock","text":"{message}","wrap":true}]}}]}""",
        ["slack"] = """{"text":"{message}","mrkdwn":false,"unfurl_links":false,"unfurl_media":false}""",
        ["telegram"] = """{"chat_id":"REPLACE_WITH_CHAT_ID","text":"{message}","disable_notification":false}""",
        ["signal"] = """{"message":"{message}","number":"REPLACE_WITH_SENDER","recipients":["REPLACE_WITH_RECIPIENT"]}"""
    };

    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        if (template.Length > 16384) throw new AutomationException("Webhook templates must be at most 16 KiB.");
        JsonNode node;
        try { node = JsonNode.Parse(template) ?? throw new JsonException(); }
        catch (JsonException) { throw new AutomationException("The message template must be valid JSON."); }
        if (node is not JsonObject) throw new AutomationException("The message template must be a JSON object.");
        void Visit(JsonNode current)
        {
            if (current is JsonObject obj)
                foreach (var key in obj.Select(x => x.Key).ToArray()) obj[key] = Replace(obj[key]);
            else if (current is JsonArray array)
                for (var i = 0; i < array.Count; i++) array[i] = Replace(array[i]);
        }
        JsonNode? Replace(JsonNode? value)
        {
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                // One pass: user-controlled action names cannot expand additional template tokens.
                return JsonValue.Create(System.Text.RegularExpressions.Regex.Replace(text, "\\{([a-z]+)\\}", match => values.GetValueOrDefault(match.Groups[1].Value, match.Value)));
            }
            if (value is not null) Visit(value);
            return value?.DeepClone();
        }
        Visit(node);
        var json = node.ToJsonString();
        if (json.Length > 65536) throw new AutomationException("Rendered webhook message is too large.");
        return json;
    }
}

public sealed class OutgoingWebhookService(StanzaDbContext db, IDataProtectionProvider protection, TimeProvider clock)
{
    private const string Key = "outgoing-webhooks.v1";
    private readonly IDataProtector protector = protection.CreateProtector("Stanza.OutgoingWebhooks.v1");
    internal async Task<OutgoingConfiguration> ConfigurationAsync(CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == Key, ct);
        return row is null ? new(0, []) : JsonSerializer.Deserialize<OutgoingConfiguration>(protector.Unprotect(row.Value))
            ?? throw new AutomationException("Outgoing webhook configuration could not be read.");
    }
    internal async Task RequireAdmin(Guid actor, CancellationToken ct)
    {
        if (!await db.Accounts.AnyAsync(x => x.Id == actor && x.Enabled && (x.Role == AccountRole.Admin || x.Role == AccountRole.SystemAdmin), ct))
            throw new AutomationAccessException();
    }
    public async Task<OutgoingPage> ReadAsync(Guid actor, CancellationToken ct = default)
    {
        await RequireAdmin(actor, ct);
        var config = await ConfigurationAsync(ct);
        // Webhook URLs often contain bearer secrets; never round-trip them through HTML.
        return new(config with { Channels = config.Channels.Select(x => x with { Url = "" }).ToArray() },
            await db.Set<OutgoingDelivery>().AsNoTracking().OrderByDescending(x => x.NextAttemptAt).Take(100).ToArrayAsync(ct));
    }
    public async Task SaveAsync(Guid actor, OutgoingEditor input, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct); await RequireAdmin(actor, ct);
        var config = await ConfigurationAsync(ct);
        if (config.Revision != input.Revision) throw new AutomationException("Channels changed. Reload before saving.");
        var old = input.Id is { } id ? config.Channels.SingleOrDefault(x => x.Id == id) ?? throw new AutomationException("Channel unavailable.") : null;
        if (old is null && config.Channels.Count >= 100) throw new AutomationException("At most 100 channels are supported.");
        if (!OutgoingTemplates.Defaults.ContainsKey(input.Provider)) throw new AutomationException("Select a supported provider.");
        var name = AutomationRules.Name(input.Name);
        var url = string.IsNullOrWhiteSpace(input.Url) ? old?.Url : input.Url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || url!.Length > 4096)
            throw new AutomationException("Supply an HTTPS webhook/gateway URL without user info or fragments.");
        var template = string.IsNullOrWhiteSpace(input.Template) ? OutgoingTemplates.Defaults[input.Provider] : input.Template;
        OutgoingTemplates.Render(template, new Dictionary<string, string>());
        var channel = new OutgoingChannel(old?.Id ?? Guid.NewGuid(), name, input.Provider, url, template, input.Enabled,
            input.Started, input.Succeeded, input.Failed, old?.CreatedAt ?? clock.GetUtcNow().ToUnixTimeSeconds());
        await PersistAsync(actor, new(config.Revision + 1, config.Channels.Where(x => x.Id != channel.Id).Append(channel).ToArray()), "saved", channel.Id, ct);
        await transaction.CommitAsync(ct);
    }
    public async Task DeleteAsync(Guid actor, Guid id, long revision, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct); await RequireAdmin(actor, ct);
        var config = await ConfigurationAsync(ct);
        if (revision != config.Revision) throw new AutomationException("Channels changed. Reload before deleting.");
        await PersistAsync(actor, new(config.Revision + 1, config.Channels.Where(x => x.Id != id).ToArray()), "deleted", id, ct);
        await transaction.CommitAsync(ct);
    }
    private async Task PersistAsync(Guid actor, OutgoingConfiguration config, string operation, Guid channel, CancellationToken ct)
    {
        var row = await db.Settings.SingleOrDefaultAsync(x => x.Key == Key, ct);
        if (row is null) { row = new() { Key = Key }; db.Add(row); }
        row.Value = protector.Protect(JsonSerializer.Serialize(config));
        db.Add(new Audit.AuditEvent { Id = Guid.NewGuid(), ActorId = actor, Action = "automation.outgoing." + operation, Resource = channel.ToString(), Result = "saved", At = clock.GetUtcNow().ToUnixTimeSeconds() });
        await db.SaveChangesAsync(ct);
    }

    public async Task CaptureAsync(CancellationToken ct = default)
    {
        // Disabled/unconfigured notifications must not take the global execution/security lock.
        if (!(await ConfigurationAsync(ct)).Channels.Any(x => x.Enabled)) return;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        var channels = (await ConfigurationAsync(ct)).Channels.Where(x => x.Enabled).ToArray();
        if (channels.Length == 0) return;
        var actions = await db.Set<ActionDefinition>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var deployments = await db.Set<Deployments.DeploymentDraftEntity>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        foreach (var channel in channels)
        {
            // Select only uncaptured events and bounded metadata, never encrypted command snapshots.
            var runs = await db.Set<AutomationRun>().AsNoTracking().Where(run => run.CreatedAt >= channel.CreatedAt && run.ActionId != null &&
                (channel.Started && run.StartedAt != null && !db.Set<OutgoingDelivery>().Any(d => d.ChannelId == channel.Id && d.RunId == run.Id && d.Event == "started") ||
                 run.CompletedAt != null && run.State != "queued" && run.State != "running" &&
                 (run.State == "succeeded" ? channel.Succeeded : channel.Failed) &&
                 !db.Set<OutgoingDelivery>().Any(d => d.ChannelId == channel.Id && d.RunId == run.Id && d.Event == (run.State == "succeeded" ? "succeeded" : "failed"))))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(200).Select(x => new AutomationRun {
                    Id = x.Id, ActionId = x.ActionId, DeploymentId = x.DeploymentId, State = x.State, Source = x.Source, StartedAt = x.StartedAt, CompletedAt = x.CompletedAt
                }).ToListAsync(ct);
            foreach (var run in runs)
            foreach (var ev in Events(run, channel))
            {
                if (await db.Set<OutgoingDelivery>().AnyAsync(x => x.ChannelId == channel.Id && x.RunId == run.Id && x.Event == ev, ct)) continue;
                var action = actions.GetValueOrDefault(run.ActionId!.Value, "Deleted action");
                var deployment = run.DeploymentId is { } id ? deployments.GetValueOrDefault(id, "Deleted deployment") : "Global";
                var values = new Dictionary<string, string> { ["action"] = action, ["deployment"] = deployment, ["event"] = ev,
                    ["state"] = ev == "started" ? "running" : run.State, ["source"] = run.Source, ["run"] = run.Id.ToString(),
                    ["message"] = $"Stanza: {action} / {deployment} — {ev} ({(ev == "started" ? "running" : run.State)}). Run {run.Id}. Trigger: {run.Source}." };
                db.Add(new OutgoingDelivery { Id = Guid.NewGuid(), ChannelId = channel.Id, RunId = run.Id, Event = ev,
                    ProtectedBody = protector.Protect(OutgoingTemplates.Render(channel.Template, values)), NextAttemptAt = clock.GetUtcNow().ToUnixTimeSeconds() });
            }
        }
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
    public static IEnumerable<string> Events(AutomationRun run, OutgoingChannel channel)
    {
        if (channel.Started && run.StartedAt.HasValue) yield return "started";
        if (run.CompletedAt.HasValue && run.State is not ("queued" or "running"))
        {
            if (run.State == "succeeded" && channel.Succeeded) yield return "succeeded";
            else if (run.State != "succeeded" && channel.Failed) yield return "failed";
        }
    }

    public async Task<bool> SendNextAsync(HttpClient client, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var item = await db.Set<OutgoingDelivery>().AsNoTracking().Where(x => (x.State == "pending" || x.State == "sending") && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Event == "started" ? 0 : 1).FirstOrDefaultAsync(ct);
        if (item is null) return false;
        if (item.Attempts >= 5)
        {
            await db.Set<OutgoingDelivery>().Where(x => x.Id == item.Id && x.Attempts >= 5 && x.NextAttemptAt <= now && (x.State == "pending" || x.State == "sending"))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "failed").SetProperty(x => x.Result, "attempt_limit"), ct);
            return true;
        }
        if (await db.Set<OutgoingDelivery>().Where(x => x.Id == item.Id && x.NextAttemptAt <= now && (x.State == "pending" || x.State == "sending"))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "sending").SetProperty(x => x.NextAttemptAt, now + 60).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct) != 1) return true;
        var channel = (await ConfigurationAsync(ct)).Channels.SingleOrDefault(x => x.Id == item.ChannelId && x.Enabled);
        var result = "channel_disabled"; var succeeded = false; var permanent = channel is null;
        if (channel is not null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, channel.Url) { Content = new StringContent(protector.Unprotect(item.ProtectedBody), System.Text.Encoding.UTF8, "application/json") };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                succeeded = response.IsSuccessStatusCode;
                result = "http_" + (int)response.StatusCode;
                permanent = (int)response.StatusCode is >= 300 and < 500 && (int)response.StatusCode is not (408 or 429);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { result = "transport_failed"; }
        }
        // At-least-once delivery: a process crash after a remote accepts can result in a duplicate.
        using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await db.Set<OutgoingDelivery>().Where(x => x.Id == item.Id && x.State == "sending").ExecuteUpdateAsync(u =>
            u.SetProperty(x => x.State, succeeded ? "sent" : permanent || item.Attempts >= 4 ? "failed" : "pending")
                .SetProperty(x => x.Result, result).SetProperty(x => x.NextAttemptAt, now + (long)Math.Pow(2, Math.Min(item.Attempts, 8)) * 30), persist.Token);
        return true;
    }
}

public sealed class OutgoingWebhookWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OutgoingWebhookWorker> logger,
    Configuration.InstanceMaintenance maintenance) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!maintenance.Paused)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<OutgoingWebhookService>();
                    await service.CaptureAsync(stoppingToken);
                    for (var i = 0; i < 100 && !maintenance.Paused; i++) if (!await service.SendNextAsync(client, stoppingToken)) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogError("Outgoing webhook processing failed. Endpoint URLs and message bodies are not logged."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
