using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Security;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Configuration;

public sealed record InstanceSettings(string PublicUrl, string GlobalEnvironment, long Version)
{
    public override string ToString() => $"InstanceSettings {{ Version = {Version}, GlobalEnvironment = [REDACTED] }}";
}

public sealed class SettingsOperationException(string message) : Exception(message);

public sealed class InstanceSettingsService(StanzaDbContext store, IDataProtectionProvider protection, BrowserHttpPolicy? browserHttp = null)
{
    private const string Key = "instance-settings-v1";
    private readonly IDataProtector protector = protection.CreateProtector("Stanza.InstanceSettings.v1");

    internal async Task<InstanceSettings> ReadInternalAsync(CancellationToken token = default)
    {
        var row = await store.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == Key, token);
        return row is null ? new("", "", 0) : Decode(row.Value);
    }
    public Task<bool> CanAsync(Guid actor, string action, CancellationToken ct = default) =>
        new AccessService(store, TimeProvider.System).IsAllowedAsync(actor, action, "instance", null, ct);

    // Transport components need only the administrator-configured public origin, never environment values.
    public async Task<string?> ReadPublicOriginAsync(CancellationToken token = default)
    {
        var row = await store.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == Key, token);
        if (row is null) return null;
        var url = Decode(row.Value).PublicUrl;
        return string.IsNullOrWhiteSpace(url) ? null : new Uri(url).GetLeftPart(UriPartial.Authority);
    }

    public async Task<InstanceSettings> ReadAsync(Guid actorId, CancellationToken token = default)
    {
        if (!await CanAsync(actorId, "settings.read", token) && !await CanAsync(actorId, "environment.reveal", token) && !await CanAsync(actorId, "environment.write", token))
            throw new SettingsOperationException("Settings or environment access is required.");
        var current = await ReadInternalAsync(token);
        return await CanAsync(actorId, "environment.reveal", token) ? current : current with { GlobalEnvironment = "" };
    }

    public async Task SaveAsync(Guid actorId, InstanceSettings settings, CancellationToken token = default)
        => await SaveEditorAsync(actorId, settings.PublicUrl, settings.GlobalEnvironment, settings.Version, token);

    public async Task SaveEditorAsync(Guid actorId, string? publicUrl, string? environment, long version, CancellationToken token = default)
    {
        await using var transaction = await store.Database.BeginTransactionAsync(token);
        // Share the account mutation guard so authorization cannot race an account revocation.
        await store.Database.ExecuteSqlRawAsync("UPDATE \"SecurityGuard\" SET \"Revision\" = \"Revision\" + 1 WHERE \"Id\" = 1", token);
        if (publicUrl is not null && !await CanAsync(actorId, "settings.write", token)) throw new SettingsOperationException("Settings write access is required.");
        if (environment is not null && !await CanAsync(actorId, "environment.write", token)) throw new SettingsOperationException("Environment write access is required.");
        if (publicUrl is null && environment is null) throw new SettingsOperationException("No editable settings were submitted.");
        var original = await ReadInternalAsync(token);
        var settings = new InstanceSettings(publicUrl ?? original.PublicUrl, environment ?? original.GlobalEnvironment, version);
        ValidateConfiguration(settings);
        var row = await store.Settings.SingleOrDefaultAsync(x => x.Key == Key, token);
        var currentVersion = original.Version;
        if (settings.Version != currentVersion)
            throw new SettingsOperationException("Settings were changed by another user. Reload before saving.");
        var value = protector.Protect(System.Text.Json.JsonSerializer.Serialize(settings with { Version = checked(currentVersion + 1) }));
        if (row is null) store.Settings.Add(new SettingEntity { Key = Key, Value = value });
        else row.Value = value;
        await store.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public void ValidateConfiguration(InstanceSettings settings) => Validate(settings, browserHttp?.AllowInsecureHttp == true);

    public static void Validate(InstanceSettings settings, bool allowInsecureHttp = false)
    {
        if (settings.PublicUrl.Length > 2048 || (!string.IsNullOrWhiteSpace(settings.PublicUrl)
            && (!Uri.TryCreate(settings.PublicUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || allowInsecureHttp)))
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || uri.AbsolutePath != "/")))
            throw new SettingsOperationException("Use an HTTPS origin without a path, credentials, query, or fragment. Non-loopback HTTP requires the pre-launch STANZA_ALLOW_INSECURE_HTTP opt-in.");
        if (settings.Version < 0 || Encoding.UTF8.GetByteCount(settings.GlobalEnvironment) > 1024 * 1024 || settings.GlobalEnvironment.Contains('\0'))
            throw new SettingsOperationException("Environment text must be valid text no larger than 1 MiB.");
    }

    private InstanceSettings Decode(string value) =>
        System.Text.Json.JsonSerializer.Deserialize<InstanceSettings>(protector.Unprotect(value))
        ?? throw new SettingsOperationException("Stored settings could not be read.");

}
