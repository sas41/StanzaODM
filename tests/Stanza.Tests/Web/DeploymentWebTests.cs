using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Stanza.Core.Security;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Security;

namespace Stanza.Tests.Web;

public sealed class DeploymentWebTests
{
    [Fact]
    public async Task AdminCanCreateDraftEditFilesAndReadImmutableHistory()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var ownerId = await factory.BootstrapAsync();
        await Login(client, "owner");
        var csrf = await Token(client, "/deployments/create");
        const string compose = "services:\r\n  web:\r\n    image: nginx:alpine\r\n";
        var created = await client.PostAsync("/deployments/create", Form(("__RequestVerificationToken", csrf), ("Name", "Web application"), ("ComposeText", compose), ("EnvironmentText", "TOKEN=private-deployment-value\r\n")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.StartsWith("/deployments/", created.Headers.Location?.ToString());
        Guid id;
        using (var scope = factory.Services.CreateScope())
            id = Assert.Single(await scope.ServiceProvider.GetRequiredService<DeploymentDraftService>().ListAsync(ownerId)).Id;
        var details = await client.GetStringAsync($"/deployments/{id}");
        Assert.Contains("Web application", details);
        Assert.Contains("private-deployment-value", details);
        csrf = await Token(client, $"/deployments/{id}/file");
        var saved = await client.PostAsync($"/deployments/{id}/file", Form(("__RequestVerificationToken", csrf), ("ExpectedRevision", "1"), ("Path", "Dockerfile"), ("Text", "FROM nginx:alpine\n"), ("IsNew", "true")));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var file = await client.GetByteArrayAsync($"/deployments/{id}/download?path=Dockerfile");
        Assert.Equal("FROM nginx:alpine\n", System.Text.Encoding.UTF8.GetString(file));
        var original = await client.GetByteArrayAsync($"/deployments/{id}/download-definition?revision=1");
        Assert.Equal(compose, System.Text.Encoding.UTF8.GetString(original));
        var oldHistory = await client.GetStringAsync($"/deployments/{id}?revision=1");
        Assert.DoesNotContain("download?path=Dockerfile", oldHistory);
    }

    [Fact]
    public async Task UserGrantsDoNotExposeDeploymentEnvironmentOrUnrelatedDrafts()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var owner = await factory.BootstrapAsync();
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            var access = scope.ServiceProvider.GetRequiredService<AccessService>();
            var drafts = scope.ServiceProvider.GetRequiredService<DeploymentDraftService>();
            var user = await accounts.CreateAsync(owner, "reader", "a-secure-password-123", AccountRole.User);
            id = (await drafts.CreateAsync(owner, "Allowed draft", null, "services: {}\n", "SECRET=hidden-value\n")).Id;
            await drafts.CreateAsync(owner, "Forbidden draft", null, "services: {}\n", "SECRET=other-hidden-value\n");
            await access.AddGrantAsync(owner, user.Id, null, "deployment.read", "deployment", id);
            await access.AddGrantAsync(owner, user.Id, null, "files.read", "deployment", id);
        }
        await Login(client, "reader");
        var list = await client.GetStringAsync("/deployments");
        Assert.Contains("Allowed draft", list);
        Assert.DoesNotContain("Forbidden draft", list);
        var details = await client.GetStringAsync($"/deployments/{id}");
        Assert.Contains("services:", details);
        Assert.DoesNotContain("hidden-value", details);
        var environment = await client.GetAsync($"/deployments/{id}/download-definition?environment=true");
        Assert.Equal(HttpStatusCode.Redirect, environment.StatusCode);
        Assert.Contains("denied", environment.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/deployments/{id}/edit")).StatusCode);
    }

    private static async Task Login(HttpClient client, string username)
    {
        var csrf = await Token(client, "/account/login");
        var result = await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", username), ("Password", "a-secure-password-123")));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
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
