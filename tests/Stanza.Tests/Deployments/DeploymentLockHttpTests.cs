using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Deployments;
using Stanza.Tests.Automation;
using Stanza.Tests.Web;

namespace Stanza.Tests.Deployments;

public sealed class DeploymentLockHttpTests
{
    [Fact]
    public async Task DeploymentPageCanLockAndUnlockWithCsrfAndDisplaysOwnerReasonExpiryAndConfiguredMaximum()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync(); Guid deployment;
        using (var scope = factory.Services.CreateScope())
            deployment = (await scope.ServiceProvider.GetRequiredService<DeploymentDraftService>().CreateAsync(owner, "Lockable", null, "services: {}", "")).Id;
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); await AutomationHttpTests.Login(client, "owner");
        var route = $"/deployments/{deployment}";
        Assert.Contains("168", await client.GetStringAsync(route));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(route + "/lock", AutomationHttpTests.Form(("hours", "1")))).StatusCode);
        var token = await AutomationHttpTests.Token(client, route);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(route + "/lock", AutomationHttpTests.Form(("__RequestVerificationToken", token), ("hours", "2"), ("description", "QA maintenance")))).StatusCode);
        var page = await client.GetStringAsync(route); Assert.Contains("Locked by", page); Assert.Contains("owner", page); Assert.Contains("QA maintenance", page);
        Assert.Contains("Unlock deployment", page);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(route + "/unlock", AutomationHttpTests.Form())).StatusCode);
        token = await AutomationHttpTests.Token(client, route);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(route + "/unlock", AutomationHttpTests.Form(("__RequestVerificationToken", token)))).StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.Null((await check.ServiceProvider.GetRequiredService<DeploymentLockService>().ReadAsync(owner, deployment)).Current);
    }
}
