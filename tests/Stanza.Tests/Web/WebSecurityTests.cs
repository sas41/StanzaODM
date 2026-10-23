using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stanza.ControlPlane;
using Stanza.Core.Security;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Web;

public sealed class WebSecurityTests
{
    [Fact]
    public async Task HealthIsAnonymousButDashboardRequiresAuthentication()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
        Assert.Contains("/account/login", dashboard.Headers.Location?.ToString());
    }

    [Fact]
    public async Task SetupRequiresCsrfAndCannotBeClaimedTwice()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var rejected = await client.PostAsync("/setup", Form(("Username", "owner"), ("Password", "a-secure-password-123"), ("ConfirmPassword", "a-secure-password-123")));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var csrf = await Token(client, "/setup");
        var created = await client.PostAsync("/setup", Form(("__RequestVerificationToken", csrf), ("Username", "owner"), ("Password", "a-secure-password-123"), ("ConfirmPassword", "a-secure-password-123")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var setupAgain = await client.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.Redirect, setupAgain.StatusCode);
        using var scope = factory.Services.CreateScope();
        var accounts = await scope.ServiceProvider.GetRequiredService<AccountService>().ListAsync();
        Assert.Single(accounts);
        Assert.Equal(AccountRole.SystemAdmin, accounts[0].Role);
    }

    [Fact]
    public async Task LoginRejectsExternalRedirectAndLogoutRequiresCsrf()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.BootstrapAsync();
        var csrf = await Token(client, "/account/login");
        var result = await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", "owner"), ("Password", "a-secure-password-123"), ("ReturnUrl", "https://example.invalid/phishing")));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Equal("/", result.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/account/logout", Form())).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/account/logout")).StatusCode);
    }

    [Fact]
    public async Task OrdinaryUsersCannotManageAccountsAndDisabledSessionsAreRejected()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var ownerId = await factory.BootstrapAsync();
        Guid userId;
        using (var scope = factory.Services.CreateScope())
            userId = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(ownerId, "ordinary", "a-secure-password-123", AccountRole.User)).Id;
        var csrf = await Token(client, "/account/login");
        await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", "ordinary"), ("Password", "a-secure-password-123")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        var denied = await client.GetAsync("/users");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("denied", denied.Headers.Location?.ToString());
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AccountService>().SetEnabledAsync(ownerId, userId, false);
        var dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
        Assert.Contains("login", dashboard.Headers.Location?.ToString());
    }

    [Fact]
    public async Task SecurityHeadersAndSelfHostedAssetsAreAvailable()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient();
        var page = await client.GetAsync("/setup");
        Assert.Contains("frame-ancestors 'none'", string.Join(" ", page.Headers.GetValues("Content-Security-Policy")));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("/vendor/htmx.min.js", html);
        Assert.DoesNotContain("https://unpkg", html);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/vendor/htmx.min.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/css/stanza.css")).StatusCode);
    }

    [Fact]
    public async Task SessionsExpireWithoutSlidingRenewal()
    {
        var clock = new AdjustableClock();
        await using var factory = new StanzaFactory(clock);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.BootstrapAsync();
        var csrf = await Token(client, "/account/login");
        await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", "owner"), ("Password", "a-secure-password-123")));
        clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task LoginAttemptsAreThrottledWithoutReflectingPasswords()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.BootstrapAsync();
        var csrf = await Token(client, "/account/login");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var response = await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", "owner"), ("Password", "wrong-private-password")));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("wrong-private-password", await response.Content.ReadAsStringAsync());
        }
        var throttled = await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", "owner"), ("Password", "wrong-private-password")));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));

    private static async Task<string> Token(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "An antiforgery field should be present.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}

public sealed class StanzaFactory : WebApplicationFactory<ControlPlaneEntryPoint>
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"stanza-web-test-{Guid.NewGuid():N}");
    private readonly TimeProvider? clock;
    private readonly string environment;
    private readonly bool allowInsecureHttp;

    public StanzaFactory(TimeProvider? clock = null, string environment = "Testing", bool allowInsecureHttp = false)
    {
        this.clock = clock;
        this.environment = environment;
        this.allowInsecureHttp = allowInsecureHttp;
        connection.Open();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Stanza"] = "Host=unused;Database=stanza;Username=test;Password=unused",
            ["Stanza:DataRoot"] = dataRoot,
            ["Stanza:AllowInsecureHttp"] = allowInsecureHttp.ToString()
        }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Stanza"] = "Host=unused;Database=stanza;Username=test;Password=unused",
            ["Stanza:DataRoot"] = dataRoot,
            ["Stanza:AllowInsecureHttp"] = allowInsecureHttp.ToString()
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<StanzaDbContext>();
            services.RemoveAll<DbContextOptions<StanzaDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<StanzaDbContext>>();
            services.AddDbContext<StanzaDbContext>(options => options.UseSqlite(connection));
            if (clock is not null)
                services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options => options.TimeProvider = clock);
        });
    }

    public async Task<Guid> BootstrapAsync()
    {
        using var scope = Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await connection.DisposeAsync();
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        GC.SuppressFinalize(this);
    }
}

internal sealed class AdjustableClock : TimeProvider
{
    private readonly DateTimeOffset initial = DateTimeOffset.UtcNow;
    private long offset;
    public override DateTimeOffset GetUtcNow() => initial.AddTicks(Interlocked.Read(ref offset));
    public void Advance(TimeSpan duration) => Interlocked.Add(ref offset, duration.Ticks);
}
