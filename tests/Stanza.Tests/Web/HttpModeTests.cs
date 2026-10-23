using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Portability;
using Stanza.ControlPlane.Portability.Participants;
using Stanza.ControlPlane.Security;

namespace Stanza.Tests.Web;

public sealed class HttpModeTests
{
    [Theory]
    [InlineData(false, "http", true, HttpStatusCode.Redirect)]
    [InlineData(false, "https", true, HttpStatusCode.OK)]
    [InlineData(true, "http", false, HttpStatusCode.OK)]
    [InlineData(true, "https", true, HttpStatusCode.OK)]
    public async Task Production_sessions_require_secure_transport_unless_explicitly_opted_in(bool enabled,
        string scheme, bool secure, HttpStatusCode dashboardStatus)
    {
        await using var factory = new StanzaFactory(environment: "Production", allowInsecureHttp: enabled);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri(scheme + "://192.0.2.10:8888") });
        await factory.BootstrapAsync();
        var page = await client.GetStringAsync("/account/login");
        Assert.Equal(enabled && scheme == "http", page.Contains("data-insecure-http-warning", StringComparison.Ordinal));
        var response = await LoginAsync(client, page);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var session = Assert.Single(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith("Stanza.Session=", StringComparison.Ordinal));
        Assert.Equal(secure, session.Contains("; secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", session, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(dashboardStatus, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Http_opt_in_retains_CSRF_and_session_revocation_and_exposes_read_only_bootstrap_state()
    {
        await using var factory = new StanzaFactory(environment: "Production", allowInsecureHttp: true);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://192.0.2.10:8888") });
        var owner = await factory.BootstrapAsync();
        await LoginAsync(client, await client.GetStringAsync("/account/login"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/account/logout", new FormUrlEncodedContent([]))).StatusCode);
        var settings = await client.GetStringAsync("/settings");
        Assert.Contains("STANZA_ALLOW_INSECURE_HTTP", settings);
        Assert.Contains("Enabled", settings);
        Assert.DoesNotContain("name=\"AllowInsecureHttp\"", settings);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AccountService>().ChangePasswordAsync(owner, "a-secure-password-123", "a-different-secure-password-456");
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_origin_edit_and_import_use_the_same_bootstrap_policy(bool enabled)
    {
        await using var factory = new StanzaFactory(environment: "Production", allowInsecureHttp: enabled);
        var owner = await factory.BootstrapAsync();
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<InstanceSettingsService>();
        var participant = Assert.Single(scope.ServiceProvider.GetServices<IPortableConfigurationParticipant>(), x => x.Kind == "instance-settings");
        var context = new PortableParticipantImportContext(owner,
            new(new Dictionary<string, Guid>(), new Dictionary<string, string>(), new Dictionary<string, string>()), new Dictionary<string, Guid>());
        var section = JsonSerializer.SerializeToElement(new PortablePublicSettings("http://192.0.2.10:8888"));
        if (enabled)
        {
            await settings.SaveAsync(owner, new("http://control.example.test:8888", "", 0));
            await participant.ApplyAsync(section, context, default);
            Assert.Equal("http://192.0.2.10:8888", (await settings.ReadAsync(owner)).PublicUrl);
        }
        else
        {
            await Assert.ThrowsAsync<SettingsOperationException>(() => settings.SaveAsync(owner, new("http://192.0.2.10:8888", "", 0)));
            await Assert.ThrowsAsync<SettingsOperationException>(() => participant.ValidateAsync(section, context, default));
        }
        var downgrade = JsonSerializer.SerializeToElement(new { publicUrl = "http://192.0.2.10:8888", allowInsecureHttp = true });
        await Assert.ThrowsAsync<PortabilityException>(() => participant.ValidateAsync(downgrade, context, default));
        Assert.Equal(enabled, scope.ServiceProvider.GetRequiredService<BrowserHttpPolicy>().AllowInsecureHttp);
    }

    [Theory]
    [InlineData("http://user:password@control.example.test")]
    [InlineData("http://control.example.test/path")]
    [InlineData("http://control.example.test?token=value")]
    [InlineData("http://control.example.test/#fragment")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://control.example.test")]
    public void Http_opt_in_does_not_allow_invalid_origins(string origin) =>
        Assert.Throws<SettingsOperationException>(() => InstanceSettingsService.Validate(new(origin, "", 0), allowInsecureHttp: true));

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string page)
    {
        var csrf = WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        return client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["Username"] = "owner", ["Password"] = "a-secure-password-123"
        }));
    }
}
