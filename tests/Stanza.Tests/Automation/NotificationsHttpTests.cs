using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Automation;

public sealed class NotificationsHttpTests
{
    [Fact]
    public async Task AdminCanConfigureAllProvidersThroughTheUiWithoutExposingEndpointSecretsAndMutationsRequireCsrf()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); await AutomationHttpTests.Login(client, "owner");
        var page = await client.GetStringAsync("/settings/notifications");
        foreach (var provider in OutgoingTemplates.Defaults.Keys) Assert.Contains(provider, page);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/settings/notifications", AutomationHttpTests.Form(("Name", "No CSRF")))).StatusCode);
        var csrf = await AutomationHttpTests.Token(client, "/settings/notifications");
        var response = await client.PostAsync("/settings/notifications", AutomationHttpTests.Form(("__RequestVerificationToken", csrf),
            ("Revision", "0"), ("Name", "Discord QA"), ("Provider", "discord"), ("Url", "https://example.test/endpoint-secret"), ("Enabled", "true"), ("Succeeded", "true"), ("Failed", "true")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        page = await client.GetStringAsync("/settings/notifications"); Assert.Contains("Discord QA", page); Assert.DoesNotContain("endpoint-secret", page);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OutgoingWebhookService>(); var saved = await service.ReadAsync(owner);
        var channel = Assert.Single(saved.Configuration.Channels);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/settings/notifications/{channel.Id}/delete", AutomationHttpTests.Form(("revision", "1")))).StatusCode);
        await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(owner, "qa", "a-secure-password-123", AccountRole.User);
        using var qa = factory.CreateClient(new() { AllowAutoRedirect = false }); await AutomationHttpTests.Login(qa, "qa");
        Assert.Equal(HttpStatusCode.Redirect, (await qa.GetAsync("/settings/notifications")).StatusCode);
        csrf = await AutomationHttpTests.Token(qa, "/account/password");
        Assert.Equal(HttpStatusCode.Redirect, (await qa.PostAsync($"/settings/notifications/{channel.Id}/delete", AutomationHttpTests.Form(("__RequestVerificationToken", csrf), ("revision", "1")))).StatusCode);
        Assert.Single((await service.ReadAsync(owner)).Configuration.Channels);
    }
}
