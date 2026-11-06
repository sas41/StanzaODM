using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Configuration;

public sealed class AgentConfigurationHttpTests
{
    [Fact]
    public async Task AgentConcurrencyAndDiscoveryAreConfigurableThroughTheUiWithoutAGlobalExecutionCap()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        var agent = await AddAgentAsync(factory, "capacity-agent", "");
        using var client = ConfigurationHttpTests.Client(factory); await ConfigurationHttp.LoginAsync(client, "owner");
        var route = $"/agents/{agent}/policy"; var page = await client.GetStringAsync(route);
        foreach (var field in new[] { "maximumParallelOperations", "maximumParallelDiscovery", "discoveryTimeoutSeconds" }) Assert.Contains("name=\"" + field + "\"", page);
        Assert.DoesNotContain("name=\"Policy.MaximumParallelOperations\"", await client.GetStringAsync("/settings"));
        var csrf = await ConfigurationHttp.TokenAsync(client, route);
        var saved = await client.PostAsync(route, ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("version", "0"),
            ("executionEnabled", "true"), ("dockerEndpoint", "unix:///var/run/docker.sock"), ("maximumParallelOperations", "3"), ("maximumParallelDiscovery", "2"), ("discoveryTimeoutSeconds", "17")));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using var scope = factory.Services.CreateScope(); var policy = await scope.ServiceProvider.GetRequiredService<AgentPolicyService>().ReadAsync(owner, agent);
        Assert.Equal(3, policy.Policy.MaximumParallelOperations); Assert.Equal(2, policy.Policy.MaximumParallelDiscovery); Assert.Equal(17, policy.Policy.DiscoveryTimeoutSeconds);
    }

    [Fact]
    public async Task ScopedAgentReadAndManageWorkThroughUiWithoutImplicitEnvironmentRevealOrOtherAgentAccess()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        var first = await AddAgentAsync(factory, "visible-agent", "FIRST=hidden-secret"); var second = await AddAgentAsync(factory, "other-agent", "SECOND=hidden-secret");
        var actor = await ConfigurationHttp.ActorAsync(factory, owner, "delegate");
        await GrantAsync(factory, owner, actor, "agent.read", first); await GrantAsync(factory, owner, actor, "agent.manage", first);
        using var client = ConfigurationHttpTests.Client(factory); await ConfigurationHttp.LoginAsync(client, "delegate");
        var list = await client.GetStringAsync("/agents"); Assert.Contains("visible-agent", list); Assert.DoesNotContain("other-agent", list); Assert.DoesNotContain("hidden-secret", list);
        var route = $"/agents/{first}/policy"; var csrf = await ConfigurationHttp.TokenAsync(client, route);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(route, PolicyForm(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(route, PolicyForm(csrf))).StatusCode);
        ConfigurationHttp.Denied(await client.GetAsync($"/agents/{second}/policy"));
        var deniedSave = await client.PostAsync($"/agents/{second}/policy", PolicyForm(csrf));
        Assert.Equal(HttpStatusCode.OK, deniedSave.StatusCode); Assert.Contains("permission required", await deniedSave.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/agents/{first}/environment")).StatusCode);
        using var verify = factory.Services.CreateScope(); var db = verify.ServiceProvider.GetRequiredService<StanzaDbContext>();
        Assert.False((await verify.ServiceProvider.GetRequiredService<AgentPolicyService>().ReadAsync(owner, first)).Policy.ExecutionEnabled);
        Assert.False(await db.Set<AgentPolicyEntity>().AnyAsync(x => x.AgentId == second));
    }

    [Fact]
    public async Task WriteOnlyAgentEnvironmentPermissionCanReplaceOnlyTheGrantedAgentWithoutRevealingSecrets()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        var first = await AddAgentAsync(factory, "first", "FIRST=existing-secret"); var second = await AddAgentAsync(factory, "second", "SECOND=retained-secret");
        var actor = await ConfigurationHttp.ActorAsync(factory, owner, "writer"); await GrantAsync(factory, owner, actor, "environment.write", first);
        using var client = ConfigurationHttpTests.Client(factory); await ConfigurationHttp.LoginAsync(client, "writer"); var csrf = await ConfigurationHttp.TokenAsync(client, "/account/password");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/agents/{first}/environment")).StatusCode);
        var saved = await client.PostAsync($"/agents/{first}/environment", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Text", "FIRST=explicit-replacement"), ("Version", "0")));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var denied = await client.PostAsync($"/agents/{second}/environment", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Text", "SECOND=forged"), ("Version", "0")));
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode); Assert.Contains("permission is required", await denied.Content.ReadAsStringAsync());
        using var verify = factory.Services.CreateScope(); var agents = verify.ServiceProvider.GetRequiredService<AgentAdministrationService>();
        Assert.Equal("FIRST=explicit-replacement", (await agents.ReadEnvironmentAsync(owner, first)).Text);
        Assert.Equal("SECOND=retained-secret", (await agents.ReadEnvironmentAsync(owner, second)).Text);
    }

    [Fact]
    public async Task AgentCredentialRotationUiRequiresCsrfAndExactManagementGrant()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        var first = await AddAgentAsync(factory, "first", ""); var second = await AddAgentAsync(factory, "second", "");
        var actor = await ConfigurationHttp.ActorAsync(factory, owner, "manager"); await GrantAsync(factory, owner, actor, "agent.manage", first); await GrantAsync(factory, owner, actor, "agent.read", first);
        using var client = ConfigurationHttpTests.Client(factory); await ConfigurationHttp.LoginAsync(client, "manager"); var csrf = await ConfigurationHttp.TokenAsync(client, "/agents");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/agents/{first}/rotate", ConfigurationHttp.Form())).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync($"/agents/{first}/rotate", ConfigurationHttp.Form(("__RequestVerificationToken", csrf)))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync($"/agents/{second}/rotate", ConfigurationHttp.Form(("__RequestVerificationToken", csrf)))).StatusCode);
        using var verify = factory.Services.CreateScope(); var db = verify.ServiceProvider.GetRequiredService<StanzaDbContext>();
        Assert.Equal("pending", (await db.Set<AgentEntity>().SingleAsync(x => x.Id == first)).Status);
        Assert.Equal("approved", (await db.Set<AgentEntity>().SingleAsync(x => x.Id == second)).Status);
        var token = Assert.Single(await db.Set<EnrollmentTokenEntity>().AsNoTracking().ToListAsync()); Assert.True(token.FirstClaimPending);
        Assert.Equal((await db.Set<AgentEntity>().SingleAsync(x => x.Id == first)).Identity, token.BoundIdentity);
    }

    private static FormUrlEncodedContent PolicyForm(string? csrf)
    {
        var values = new List<(string, string)> { ("executionEnabled", "false"), ("timeoutSeconds", "10"), ("maximumOutputCharacters", "2048"), ("dockerEndpoint", "unix:///run/custom.sock"), ("version", "0") };
        if (csrf is not null) values.Add(("__RequestVerificationToken", csrf)); return ConfigurationHttp.Form(values.ToArray());
    }
    private static async Task<Guid> AddAgentAsync(StanzaFactory factory, string hostname, string environment)
    {
        using var scope = factory.Services.CreateScope(); var id = Guid.NewGuid(); var protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var agent = new AgentEntity { Id = id, Hostname = hostname, Identity = Guid.NewGuid().ToString("N"), Status = "approved", PublicKey = "not-used-for-ui-tests", ProtectedEnvironment = protection.CreateProtector("Stanza.AgentEnvironment.v1").CreateProtector(id.ToString("N")).Protect(environment) };
        var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>(); db.Set<AgentEntity>().Add(agent); await db.SaveChangesAsync(); return id;
    }
    private static async Task GrantAsync(StanzaFactory factory, Guid owner, Guid actor, string action, Guid agent)
    { using var scope = factory.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<AccessService>().AddGrantAsync(owner, actor, null, action, "agent", agent); }
}
