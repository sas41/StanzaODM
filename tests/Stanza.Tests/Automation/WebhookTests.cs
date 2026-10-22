using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Automation;

public sealed class WebhookTests
{
    private static byte[] Body(string text = "{\"ref\":\"refs/heads/main\"}") => Encoding.UTF8.GetBytes(text);
    public static string Sign(string secret, byte[] body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    [Fact]
    public async Task SignedDeliveryIsDurableDeduplicatedAndCorrelatedWithoutPayloadHistory()
    {
        await using var s = await AutomationTestStore.Create(); var hook = await s.Hook();
        var body = Body("{\"ref\":\"refs/heads/main\",\"private\":\"secret-payload-value\"}");
        var signature = Sign(hook.Secret!, body);
        var accepted = await s.Receiver().ReceiveAsync(hook.Definition.Id, signature, "delivery-1", "push", body);
        Assert.Equal(202, accepted.Status); Assert.NotNull(accepted.RunId);
        var duplicate = await s.Receiver().ReceiveAsync(hook.Definition.Id, signature, "delivery-1", "push", body);
        Assert.Equal(200, duplicate.Status); Assert.Equal("duplicate", duplicate.Code); Assert.Equal(accepted.RunId, duplicate.RunId);
        Assert.Single(await s.Db.Set<AutomationRun>().AsNoTracking().ToListAsync());
        var delivery = Assert.Single(await s.Service.DeliveriesAsync(s.Root, hook.Definition.Id));
        Assert.DoesNotContain("secret-payload-value", System.Text.Json.JsonSerializer.Serialize(delivery));
        Assert.DoesNotContain(hook.Secret!, (await s.Db.Set<WebhookDefinition>().AsNoTracking().SingleAsync()).ProtectedSecret);
        await s.Executor().ExecuteNextAsync(); Assert.Equal("automation:" + accepted.RunId.Value.ToString("N"), Assert.Single(s.Operations.Submissions).Trigger);
    }

    [Fact]
    public void HmacUsesExactRawBytesAndRejectsMalformedOrWrongSignatures()
    {
        var body = Body(); var signature = Sign("secret", body);
        Assert.True(WebhookSecurity.Verify("secret", body, signature));
        Assert.False(WebhookSecurity.Verify("wrong", body, signature));
        Assert.False(WebhookSecurity.Verify("secret", Body("{ \"ref\":\"refs/heads/main\"}"), signature));
        foreach (var bad in new[] { "", "sha1=abcdef", "sha256=" + new string('z', 64), "sha256=" + new string('0', 64), signature + "0" })
            Assert.False(WebhookSecurity.Verify("secret", body, bad));
    }

    [Fact]
    public async Task InvalidSignatureDoesNotCreateDeliveryOrExecute()
    {
        await using var s = await AutomationTestStore.Create(); var hook = await s.Hook(); var body = Body();
        Assert.Equal(401, (await s.Receiver().ReceiveAsync(hook.Definition.Id, Sign("wrong", body), "one", "push", body)).Status);
        Assert.Empty(await s.Db.Set<WebhookDelivery>().ToListAsync()); Assert.Empty(await s.Db.Set<AutomationRun>().ToListAsync());
    }

    [Fact]
    public async Task RotationImmediatelyInvalidatesOldSecretAndRevocationDisablesEndpoint()
    {
        await using var s = await AutomationTestStore.Create(); var hook = await s.Hook(); var body = Body();
        var rotated = await s.Service.RotateWebhookAsync(s.Root, hook.Definition.Id, false);
        Assert.NotNull(rotated); Assert.NotEqual(hook.Secret, rotated);
        Assert.Equal(401, (await s.Receiver().ReceiveAsync(hook.Definition.Id, Sign(hook.Secret!, body), "old", "push", body)).Status);
        Assert.Equal(202, (await s.Receiver().ReceiveAsync(hook.Definition.Id, Sign(rotated, body), "new", "push", body)).Status);
        await s.Service.RotateWebhookAsync(s.Root, hook.Definition.Id, true);
        Assert.Equal(404, (await s.Receiver().ReceiveAsync(hook.Definition.Id, Sign(rotated, body), "revoked", "push", body)).Status);
        Assert.Equal("", (await s.Db.Set<WebhookDefinition>().AsNoTracking().SingleAsync()).ProtectedSecret);
        var revoked = await s.Db.Set<WebhookDefinition>().AsNoTracking().SingleAsync();
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SaveWebhookAsync(s.Root, new() { Id = revoked.Id,
            Name = revoked.Name, DeploymentId = revoked.DeploymentId, Enabled = true }, revoked.Revision));
    }

    [Theory]
    [InlineData("push", "refs/heads/main", "accepted", 202)]
    [InlineData("push", "refs/heads/develop", "filtered", 200)]
    [InlineData("issues", "refs/heads/main", "filtered", 200)]
    public async Task ExactEventAndRefFiltersCannotEscalateTargets(string eventName, string reference, string result, int status)
    {
        await using var s = await AutomationTestStore.Create(); var hook = await s.Hook(refs: "refs/heads/main");
        var body = Body($"{{\"ref\":\"{reference}\"}}");
        var receipt = await s.Receiver().ReceiveAsync(hook.Definition.Id, Sign(hook.Secret!, body), "id", eventName, body);
        Assert.Equal(result, receipt.Code); Assert.Equal(status, receipt.Status);
    }

    [Fact]
    public async Task PayloadAndRateLimitsBoundSignedUniqueDeliveriesAndResetWithClock()
    {
        await using var s = await AutomationTestStore.Create(); var hook = await s.Hook();
        await s.Service.SaveLimitsAsync(s.Root, new(MaximumWebhookBytes: 1024, DeliveriesPerMinute: 1));
        var oversized = Body("{\"data\":\"" + new string('x', 1100) + "\"}");
        Assert.Equal(413, (await s.Receiver().ReceiveAsync(hook.Definition.Id, Sign(hook.Secret!, oversized), "huge", "push", oversized)).Status);
        var body = Body(); var signature = Sign(hook.Secret!, body);
        Assert.Equal(202, (await s.Receiver().ReceiveAsync(hook.Definition.Id, signature, "first", "push", body)).Status);
        Assert.Equal(200, (await s.Receiver().ReceiveAsync(hook.Definition.Id, signature, "first", "push", body)).Status);
        Assert.Equal(429, (await s.Receiver().ReceiveAsync(hook.Definition.Id, signature, "second", "push", body)).Status);
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(202, (await s.Receiver().ReceiveAsync(hook.Definition.Id, signature, "second", "push", body)).Status);
    }

    [Fact]
    public async Task SafeParameterMappingPassesOneArgumentAndRejectsObjectsMissingPathsAndUnknownParameters()
    {
        await using var s = await AutomationTestStore.Create();
        var action = await s.Action(new([new("container-exec", s.OtherDeployment, Execution: new AgentExecution("printf", ["%s", "${message}"], Service: "app"))], [new("message")]));
        var hook = await s.Hook(action.Id, "{\"message\":\"commit.message\"}");
        const string attack = "$(touch /nope); --target OTHER";
        var body = Body("{\"commit\":{\"message\":\"" + attack + "\"}}");
        Assert.Equal(202, (await s.Receiver().ReceiveAsync(hook.Definition.Id, Sign(hook.Secret!, body), "safe", "push", body)).Status);
        await s.Executor().ExecuteNextAsync(); Assert.Equal(attack, Assert.Single(s.Operations.Submissions).Execution!.Arguments[1]);
        foreach (var payload in new[] { "{}", "{\"commit\":{\"message\":{}}}", "not json", "[]" })
        {
            body = Body(payload);
            Assert.Equal(400, (await s.Receiver().ReceiveAsync(hook.Definition.Id, Sign(hook.Secret!, body), Guid.NewGuid().ToString(), "push", body)).Status);
        }
        await Assert.ThrowsAsync<AutomationException>(() => s.Hook(action.Id, "{\"target\":\"commit.message\"}"));
    }

    [Fact]
    public async Task RevokedWebhookOwnerIsDeniedAtDeliveryAndQueuedExecution()
    {
        await using var s = await AutomationTestStore.Create();
        var manage = await s.Grant("automation.manage", "deployment", s.Deployment); await s.Grant("deployment.deploy", "deployment", s.Deployment);
        var hook = await s.Service.SaveWebhookAsync(s.User, new() { Name = "User hook", DeploymentId = s.Deployment, Enabled = true }, 0);
        var body = Body(); var signature = Sign(hook.Secret!, body);
        var accepted = await s.Receiver().ReceiveAsync(hook.Definition.Id, signature, "before", "push", body); Assert.Equal(202, accepted.Status);
        await s.Access.RemoveGrantAsync(s.Root, manage.Id);
        Assert.Equal(403, (await s.Receiver().ReceiveAsync(hook.Definition.Id, signature, "after", "push", body)).Status);
        await s.Executor().ExecuteNextAsync(); Assert.Empty(s.Operations.Submissions);
        Assert.Equal("denied", (await s.Service.ReadRunAsync(s.Root, accepted.RunId!.Value)).Run.State);
    }

    [Fact]
    public async Task MaximumDeliveryIdFitsDurableOccurrenceKeyAndEmptyBrowserFiltersAreSupported()
    {
        await using var s = await AutomationTestStore.Create();
        var hook = await s.Service.SaveWebhookAsync(s.Root, new() { Name = "Empty optional browser fields", DeploymentId = s.Deployment,
            Refs = null!, ParameterMapping = null!, Enabled = true }, 0);
        var body = Body();
        var accepted = await s.Receiver().ReceiveAsync(hook.Definition.Id, Sign(hook.Secret!, body), new string('x', 200), "push", body);
        Assert.Equal(202, accepted.Status);
        Assert.True((await s.Service.ReadRunAsync(s.Root, accepted.RunId!.Value)).Run.OccurrenceKey.Length <= 200);
    }
}
