using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Configuration;

public sealed class ConfigurationHttpTests
{
    [Fact]
    public async Task EveryOperationalSettingHasAUiControlAndPolicySaveRequiresCsrf()
    {
        await using var factory = new StanzaFactory(); await factory.BootstrapAsync(); using var client = Client(factory);
        using var logs = new ConfigurationDiagnosticLogs(); factory.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        await ConfigurationHttp.LoginAsync(client, "owner");
        var html = await client.GetStringAsync("/policy");
        foreach (var property in typeof(OperationalPolicy).GetProperties()) Assert.Contains($"name=\"{property.Name}\"", html);
        Assert.Contains("preserve the original Host", html);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/policy", ConfigurationHttp.Policy(new() { SessionHours = 12 }, 0))).StatusCode);
        var csrf = await ConfigurationHttp.TokenAsync(client, "/policy");
        var saved = await client.PostAsync("/policy", ConfigurationHttp.Policy(new() { SessionHours = 12, TrustedProxyAddresses = "127.0.0.1" }, 0, csrf));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using var scope = factory.Services.CreateScope(); var policy = await scope.ServiceProvider.GetRequiredService<OperationalPolicyService>().GetAsync();
        Assert.Equal(1, policy.Version); Assert.Equal(12, policy.Policy.SessionHours); Assert.Equal("127.0.0.1", policy.Policy.TrustedProxyAddresses);
        var conflict = await client.PostAsync("/policy", ConfigurationHttp.Policy(new() { SessionHours = 9, TrustedProxyAddresses = "127.0.0.1" }, 0, csrf));
        Assert.True(conflict.StatusCode == HttpStatusCode.OK, string.Join("\n", logs.Errors)); Assert.Contains("Reload before saving", await conflict.Content.ReadAsStringAsync());
        Assert.Equal(12, (await scope.ServiceProvider.GetRequiredService<OperationalPolicyService>().GetAsync()).Policy.SessionHours);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserCanSaveAnEmptyProxyListForFreshPolicyOrClearExistingProxyTrust(bool existing)
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync(); using var client = Client(factory);
        using var logs = new ConfigurationDiagnosticLogs(); factory.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        if (existing) await ConfigurationHttp.SavePolicyAsync(factory, owner, new() { TrustedProxyAddresses = "127.0.0.1" }, 0);
        await ConfigurationHttp.LoginAsync(client, "owner"); var csrf = await ConfigurationHttp.TokenAsync(client, "/policy");
        var response = await client.PostAsync("/policy", ConfigurationHttp.Policy(new(), existing ? 1 : 0, csrf));
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, string.Join("\n", logs.Errors));
        using var scope = factory.Services.CreateScope(); var policy = await scope.ServiceProvider.GetRequiredService<OperationalPolicyService>().GetAsync();
        Assert.Equal("", policy.Policy.TrustedProxyAddresses); Assert.Equal(existing ? 2 : 1, policy.Version);
    }

    [Fact]
    public async Task DelegatedPolicyReadDoesNotGrantPolicyWriteEvenWithSettingsWritePermission()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        await ConfigurationHttp.ActorAsync(factory, owner, "delegate", "settings.read", "settings.write");
        using var client = Client(factory); await ConfigurationHttp.LoginAsync(client, "delegate");
        var page = await client.GetStringAsync("/policy"); Assert.DoesNotContain("Save policy", page);
        var csrf = await ConfigurationHttp.TokenAsync(client, "/policy");
        ConfigurationHttp.Denied(await client.PostAsync("/policy", ConfigurationHttp.Policy(new(), 0, csrf)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedUrlEditsPreserveHiddenSecretsAndIgnoreForgedRevealFlags(bool environmentWriter)
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        var grants = environmentWriter ? new[] { "settings.read", "settings.write", "environment.write" } : new[] { "settings.read", "settings.write" };
        await ConfigurationHttp.ActorAsync(factory, owner, "delegate", grants);
        using (var scope = factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<InstanceSettingsService>().SaveAsync(owner, new("https://before.example", "SECRET=hidden-original-sentinel\n", 0));
        using var client = Client(factory); await ConfigurationHttp.LoginAsync(client, "delegate");
        var page = await client.GetStringAsync("/settings"); Assert.DoesNotContain("hidden-original-sentinel", page);
        if (environmentWriter) Assert.Contains("Replace the hidden environment", page);
        var csrf = await ConfigurationHttp.TokenAsync(client, "/settings");
        var response = await client.PostAsync("/settings", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Version", "1"), ("PublicUrl", "https://after.example"),
            ("GlobalEnvironment", ""), ("CanRevealEnvironment", "true"), ("ReplaceEnvironment", "false")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var verify = factory.Services.CreateScope(); var current = await verify.ServiceProvider.GetRequiredService<InstanceSettingsService>().ReadAsync(owner);
        Assert.Equal("https://after.example", current.PublicUrl); Assert.Equal("SECRET=hidden-original-sentinel\n", current.GlobalEnvironment); Assert.Equal(2, current.Version);
    }

    [Fact]
    public async Task WriteOnlyEnvironmentUserMustExplicitlyReplaceAndCannotChangePublicUrl()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        await ConfigurationHttp.ActorAsync(factory, owner, "writer", "environment.write");
        using (var scope = factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<InstanceSettingsService>().SaveAsync(owner, new("https://retained.example", "SECRET=before", 0));
        using var client = Client(factory); await ConfigurationHttp.LoginAsync(client, "writer"); var csrf = await ConfigurationHttp.TokenAsync(client, "/settings");
        var response = await client.PostAsync("/settings", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Version", "1"), ("PublicUrl", "https://forged.example"),
            ("GlobalEnvironment", "SECRET=explicitly-replaced"), ("ReplaceEnvironment", "true")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var verify = factory.Services.CreateScope(); var current = await verify.ServiceProvider.GetRequiredService<InstanceSettingsService>().ReadAsync(owner);
        Assert.Equal("https://retained.example", current.PublicUrl); Assert.Equal("SECRET=explicitly-replaced", current.GlobalEnvironment);
    }

    [Theory]
    [InlineData("settings.read")]
    [InlineData("environment.reveal")]
    public async Task ReadOrRevealOnlyActorsCannotMutateSettings(string grant)
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        await ConfigurationHttp.ActorAsync(factory, owner, "reader", grant); using var client = Client(factory); await ConfigurationHttp.LoginAsync(client, "reader");
        var csrf = await ConfigurationHttp.TokenAsync(client, "/settings");
        ConfigurationHttp.Denied(await client.PostAsync("/settings", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Version", "0"), ("PublicUrl", "https://forged.example"), ("GlobalEnvironment", "SECRET=forged"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledActorsAndRevokedScopedGrantsInvalidateExistingAuthenticatedSessions(bool disable)
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        var actor = await ConfigurationHttp.ActorAsync(factory, owner, "actor", "settings.read"); using var client = Client(factory); await ConfigurationHttp.LoginAsync(client, "actor");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/settings")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            if (disable) await scope.ServiceProvider.GetRequiredService<AccountService>().SetEnabledAsync(owner, actor, false);
            else
            {
                var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>(); var grant = await db.Grants.SingleAsync(x => x.AccountId == actor);
                await scope.ServiceProvider.GetRequiredService<AccessService>().RemoveGrantAsync(owner, grant.Id);
            }
        }
        var rejected = await client.GetAsync("/settings"); Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode); Assert.Contains("/account/login", rejected.Headers.Location!.ToString());
    }

    [Fact]
    public async Task NewSessionExpiryUsesPolicyWithoutShorteningExistingSessions()
    {
        var clock = new ManualPolicyClock(); await using var factory = new StanzaFactory(clock); var owner = await factory.BootstrapAsync();
        await ConfigurationHttp.SavePolicyAsync(factory, owner, new() { SessionHours = 2 }, 0);
        using var earlier = Client(factory); await ConfigurationHttp.LoginAsync(earlier, "owner");
        await ConfigurationHttp.SavePolicyAsync(factory, owner, new() { SessionHours = 1 }, 1);
        using var later = Client(factory); await ConfigurationHttp.LoginAsync(later, "owner");
        clock.Advance(TimeSpan.FromMinutes(90));
        Assert.Equal(HttpStatusCode.OK, (await earlier.GetAsync("/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await later.GetAsync("/settings")).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(31)); Assert.Equal(HttpStatusCode.Redirect, (await earlier.GetAsync("/settings")).StatusCode);
    }

    [Fact]
    public async Task AuthenticationAndEnrollmentRateLimitsUseUiPolicyWithoutRestart()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        await ConfigurationHttp.SavePolicyAsync(factory, owner, new() { LoginAttempts = 2, EnrollmentRequestsPerMinute = 2 }, 0);
        using var client = Client(factory); var csrf = await ConfigurationHttp.TokenAsync(client, "/account/login");
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/account/login", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Username", "owner"), ("Password", "secret-wrong-attempt")))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/account/login", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Username", "owner"), ("Password", "secret-wrong-attempt")))).StatusCode);
        for (var i = 0; i < 2; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/agent-enrollment", new { Token = "not-a-valid-token" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/agent-enrollment", new { Token = "not-a-valid-token" })).StatusCode);
        await ConfigurationHttp.SavePolicyAsync(factory, owner, new() { LoginAttempts = 3, EnrollmentRequestsPerMinute = 3 }, 1);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/account/login", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Username", "owner"), ("Password", "secret-wrong-attempt")))).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/agent-enrollment", new { Token = "not-a-valid-token" })).StatusCode);
    }

    [Fact]
    public async Task GlobalMutationAuditContainsOnlyRouteMetadataNeverBodiesEnvironmentPasswordsOrQueryValues()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync(); using var client = Client(factory);
        await ConfigurationHttp.LoginAsync(client, "owner"); var csrf = await ConfigurationHttp.TokenAsync(client, "/settings");
        var response = await client.PostAsync("/settings?private=private-query-sentinel", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Version", "0"), ("PublicUrl", "https://control.example"),
            ("GlobalEnvironment", "SECRET=private-environment-sentinel"), ("Passphrase", "private-passphrase-sentinel"), ("Output", "private-command-output-sentinel")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = factory.Services.CreateScope(); var events = await scope.ServiceProvider.GetRequiredService<AuditService>().ListAsync(owner, null);
        Assert.Contains(events, x => x.Action == "Settings.Index" && x.ActorId == owner && x.Resource == "Settings" && x.Result == "302");
        var serialized = JsonSerializer.Serialize(events);
        foreach (var forbidden in new[] { ConfigurationTestStore.Password, "private-query-sentinel", "private-environment-sentinel", "private-passphrase-sentinel", "private-command-output-sentinel", csrf }) Assert.DoesNotContain(forbidden, serialized);
        Assert.All(events, x => { Assert.InRange(x.Action.Length, 0, 200); Assert.InRange(x.Resource.Length, 0, 200); Assert.InRange(x.Result.Length, 0, 100); });
    }

    internal static HttpClient Client(StanzaFactory factory) => factory.CreateClient(new() { AllowAutoRedirect = false });
}

internal static class ConfigurationHttp
{
    public static FormUrlEncodedContent Form(params (string Key, string Value)[] values) => new(values.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));
    public static FormUrlEncodedContent Policy(OperationalPolicy policy, long version, string? csrf = null)
    {
        var values = typeof(OperationalPolicy).GetProperties().Select(p => new KeyValuePair<string, string>(p.Name, Convert.ToString(p.GetValue(policy), CultureInfo.InvariantCulture) ?? "")).ToList();
        values.Add(new("version", version.ToString(CultureInfo.InvariantCulture))); if (csrf is not null) values.Add(new("__RequestVerificationToken", csrf)); return new(values);
    }
    public static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path); var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Expected a CSRF field."); return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    public static async Task LoginAsync(HttpClient client, string username)
    {
        var csrf = await TokenAsync(client, "/account/login"); var response = await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", username), ("Password", ConfigurationTestStore.Password)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("/", response.Headers.Location!.ToString());
    }
    public static async Task<Guid> ActorAsync(StanzaFactory factory, Guid owner, string username, params string[] grants)
    {
        using var scope = factory.Services.CreateScope(); var id = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(owner, username, ConfigurationTestStore.Password, AccountRole.User)).Id;
        foreach (var grant in grants) await scope.ServiceProvider.GetRequiredService<AccessService>().AddGrantAsync(owner, id, null, grant, "instance", null); return id;
    }
    public static async Task SavePolicyAsync(StanzaFactory factory, Guid owner, OperationalPolicy policy, long version)
    { using var scope = factory.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<OperationalPolicyService>().SaveAsync(owner, new(policy, version)); }
    public static void Denied(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        if (response.StatusCode == HttpStatusCode.Redirect) Assert.Contains("denied", response.Headers.Location!.ToString());
    }
}

internal sealed class ConfigurationDiagnosticLogs : ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Errors { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Capture(Errors);
    public void Dispose() { }
    private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> errors) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(logLevel)) errors.Enqueue(formatter(state, exception) + "\n" + exception?.GetType().Name + ": " + exception?.Message + "\n" + exception?.StackTrace?.Split('\n').FirstOrDefault()); }
    }
}
