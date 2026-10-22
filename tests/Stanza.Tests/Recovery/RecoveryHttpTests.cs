using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stanza.ControlPlane;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Recovery;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;

namespace Stanza.Tests.Recovery;

public sealed class RecoveryHttpTests
{
    [Fact]
    public async Task FreshRecoveryIsAnonymousCsrfProtectedAndShowsCompleteWarnings()
    {
        await using var factory = new RecoveryWebFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var page = await client.GetAsync("/recovery");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Restore a fresh instance", html);
        Assert.Contains("password hashes", html);
        Assert.Contains("workload volumes", html);
        Assert.Contains("cannot be recovered", html);
        Assert.Contains("Restart the Control Plane", html);
        Assert.Contains("128 MiB", html);
        Assert.DoesNotContain("Download encrypted instance backup", html);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/recovery/restore", Form(("Passphrase", RecoveryServiceTests.Passphrase)))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/recovery/restore")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/recovery/backup")).StatusCode);
    }

    [Fact]
    public async Task AnonymousRestoreWorksBeforeSetupAndAllRestoreIsDeniedAfterward()
    {
        var backup = await RecoveryServiceTests.CreateBackupAsync();
        await using var factory = new RecoveryWebFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var csrf = await TokenAsync(client, "/recovery");
        using var upload = Upload(backup, csrf, RecoveryServiceTests.Passphrase);
        var response = await client.PostAsync("/recovery/restore", upload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Restore completed", html);
        Assert.Contains("restart required", html);
        Assert.DoesNotContain(RecoveryServiceTests.Passphrase, html);
        using var scope = factory.Services.CreateScope();
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<AccountService>().AuthenticateAsync("root", RecoveryServiceTests.Password));
        using var again = Upload(backup, csrf, RecoveryServiceTests.Passphrase);
        AssertDenied(await client.PostAsync("/recovery/restore", again));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/setup")).StatusCode);
    }

    [Theory]
    [InlineData(AccountRole.Admin)]
    [InlineData(AccountRole.User)]
    public async Task AdminAndOrdinaryUserCannotDownloadBackupsOrSeeRecoveryPage(AccountRole role)
    {
        await using var factory = new RecoveryWebFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.CreateAccountAsync(role);
        var csrf = await LoginAsync(client, "actor");
        AssertDenied(await client.GetAsync("/recovery"));
        var response = await client.PostAsync("/recovery/backup", Form(("__RequestVerificationToken", csrf), ("Passphrase", RecoveryServiceTests.Passphrase), ("Acknowledge", "true")));
        AssertDenied(response);
    }

    [Fact]
    public async Task SystemAdminBackupRequiresAcknowledgementAndCsrfAndDoesNotReflectPassphrase()
    {
        await using var factory = new RecoveryWebFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.CreateAccountAsync(AccountRole.SystemAdmin);
        await LoginAsync(client, "actor");
        var csrf = await TokenAsync(client, "/recovery");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/recovery/backup", Form(("Passphrase", RecoveryServiceTests.Passphrase)))).StatusCode);
        var noAck = await client.PostAsync("/recovery/backup", Form(("__RequestVerificationToken", csrf), ("Passphrase", RecoveryServiceTests.Passphrase)));
        Assert.Equal(HttpStatusCode.OK, noAck.StatusCode);
        Assert.Contains("Acknowledge", await noAck.Content.ReadAsStringAsync());
        Assert.DoesNotContain(RecoveryServiceTests.Passphrase, await noAck.Content.ReadAsStringAsync());
        var backup = await client.PostAsync("/recovery/backup", Form(("__RequestVerificationToken", csrf), ("Passphrase", RecoveryServiceTests.Passphrase), ("Acknowledge", "true")));
        Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
        Assert.Equal("application/octet-stream", backup.Content.Headers.ContentType!.MediaType);
        Assert.Contains(".stanza-recovery", backup.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("Stanza.ControlPlane", RecoveryServiceTests.Decode(await backup.Content.ReadAsByteArrayAsync())["Application"]!.GetValue<string>());
        // Even a System Admin cannot overwrite an initialized instance.
        using var restore = Upload(await backup.Content.ReadAsByteArrayAsync(), csrf, RecoveryServiceTests.Passphrase);
        AssertDenied(await client.PostAsync("/recovery/restore", restore));
    }

    [Fact]
    public async Task WrongPassphraseAndMissingAcknowledgementNeverReflectSecretsOrClaimSetup()
    {
        var backup = await RecoveryServiceTests.CreateBackupAsync();
        await using var factory = new RecoveryWebFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var csrf = await TokenAsync(client, "/recovery");
        using var upload = Upload(backup, csrf, "an-incorrect-long-passphrase");
        upload.Add(new StringContent("true"), "Restored");
        var response = await client.PostAsync("/recovery/restore", upload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("incorrect", html);
        Assert.DoesNotContain("an-incorrect-long-passphrase", html);
        Assert.DoesNotContain("Restore completed", html);
        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<RecoveryService>().CanRestoreAsync());
        using var noAck = Upload(backup, csrf, RecoveryServiceTests.Passphrase, acknowledge: false);
        var denied = await client.PostAsync("/recovery/restore", noAck);
        Assert.Contains("Acknowledge", await denied.Content.ReadAsStringAsync());
        Assert.True(await scope.ServiceProvider.GetRequiredService<RecoveryService>().CanRestoreAsync());
    }

    private static void AssertDenied(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        if (response.StatusCode == HttpStatusCode.Redirect) Assert.Contains("denied", response.Headers.Location!.ToString());
    }

    private static async Task<string> LoginAsync(HttpClient client, string username)
    {
        var csrf = await TokenAsync(client, "/account/login");
        var response = await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", username), ("Password", RecoveryServiceTests.Password)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return await TokenAsync(client, "/account/password");
    }

    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "A CSRF token must be rendered.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) => new(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));
    private static MultipartFormDataContent Upload(byte[] bytes, string csrf, string passphrase, bool acknowledge = true)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "Archive", "instance.stanza-recovery" },
            { new StringContent(csrf), "__RequestVerificationToken" },
            { new StringContent(passphrase), "Passphrase" },
            { new StringContent(acknowledge ? "true" : "false"), "Acknowledge" }
        };
        return form;
    }
}

internal sealed class RecoveryWebFactory : WebApplicationFactory<ControlPlaneEntryPoint>
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine("/tmp/opencode", "stanza-recovery-web-" + Guid.NewGuid().ToString("N"));
    public RecoveryWebFactory() => connection.Open();
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(Settings));
        return base.CreateHost(builder);
    }
    private Dictionary<string, string?> Settings => new() { ["Stanza:DataRoot"] = root, ["ConnectionStrings:Stanza"] = "Host=unused;Database=unused;Username=unused;Password=unused" };
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<StanzaDbContext>(); services.RemoveAll<DbContextOptions<StanzaDbContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<StanzaDbContext>>();
            services.AddDbContext<StanzaDbContext>(options => options.UseSqlite(connection));
            services.AddStanzaRecovery();
        });
    }
    public async Task CreateAccountAsync(AccountRole role)
    {
        using var scope = Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        var rootAccount = await accounts.BootstrapAsync("owner", RecoveryServiceTests.Password);
        await accounts.CreateAsync(rootAccount.Id, "actor", RecoveryServiceTests.Password, role);
    }
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync(); await connection.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }
}
