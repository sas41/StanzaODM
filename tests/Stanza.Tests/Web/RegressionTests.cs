using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Stanza.Core.Security;
using Stanza.ControlPlane.Security;

namespace Stanza.Tests.Web;

public sealed class RegressionTests
{
    [Fact]
    public async Task ProductionSessionCookieIsSecureEvenBehindAnHttpBackend()
    {
        await using var factory = new StanzaFactory(environment: "Production");
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.BootstrapAsync();
        var csrf = await Token(client, "/account/login");
        var response = await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", "owner"), ("Password", "a-secure-password-123")));
        var session = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Stanza.Session=", StringComparison.Ordinal));
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GroupGrantEndpointCannotRemoveDirectAccountGrants()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var owner = await factory.BootstrapAsync();
        Guid grantId;
        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(owner, "reader", "a-secure-password-123", AccountRole.User);
            userId = user.Id;
            grantId = (await scope.ServiceProvider.GetRequiredService<AccessService>().AddGrantAsync(owner, user.Id, null, "deployment.read", "deployment", null)).Id;
        }
        await Login(client);
        var csrf = await Token(client, "/groups");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/groups/grants/{grantId}/delete", Form(("__RequestVerificationToken", csrf)))).StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.Single((await check.ServiceProvider.GetRequiredService<AccessService>().GetAccessAsync(userId)).DirectGrants);
    }

    [Fact]
    public async Task RemovingGrantFromUnknownAccountReturnsNotFound()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.BootstrapAsync();
        await Login(client);
        var csrf = await Token(client, "/users");
        var response = await client.PostAsync($"/users/{Guid.NewGuid()}/grants/delete", Form(("__RequestVerificationToken", csrf), ("grantId", Guid.NewGuid().ToString())));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task Login(HttpClient client)
    {
        var csrf = await Token(client, "/account/login");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", "owner"), ("Password", "a-secure-password-123")))).StatusCode);
    }

    private static async Task<string> Token(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
}
