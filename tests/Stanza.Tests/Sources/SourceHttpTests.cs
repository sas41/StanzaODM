using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Core.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Sources;

public sealed class SourceHttpTests
{
    [Fact]
    public async Task Branch_partial_is_prefilled_searchable_preserves_missing_ref_and_shows_agent_errors()
    {
        var dispatcher = new Dispatcher { Offline = false };
        await using var original = new StanzaFactory();
        await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<SourceService>(); services.RemoveAll<IAgentCommandDispatcher>(); services.AddSingleton<IAgentCommandDispatcher>(dispatcher);
        }));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Guid deployment, source;
        using (var scope = factory.Services.CreateScope())
        {
            var root = (await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
            var agent = Guid.NewGuid(); var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
            db.Add(new AgentEntity { Id = agent, Identity = agent.ToString("N"), Hostname = "agent", Status = "approved" });
            await db.SaveChangesAsync();
            deployment = (await scope.ServiceProvider.GetRequiredService<DeploymentDraftService>().CreateAsync(root, "App", agent, "services: {}", "")).Id;
            var sources = scope.ServiceProvider.GetRequiredService<SourceService>();
            source = (await sources.SaveGitAsync(root, deployment, null, 0, new("https://git.example.test/app", "main", "src/a", null))).Id;
            await sources.SaveGitAsync(root, deployment, null, 0, new("https://git.example.test/other", "main", "src/b", null));
        }
        await Login(client, "owner");
        var details = await client.GetStringAsync($"/sources/{deployment}");
        Assert.Contains("Loading branch and tag list", details); Assert.Contains("Move earlier", details);
        var response = await client.GetAsync($"/sources/{deployment}/{source}/branches");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var partial = await response.Content.ReadAsStringAsync();
        Assert.Contains("main (saved)", partial); Assert.Contains("refs/tags/v1", partial); Assert.DoesNotContain("<html", partial);
        Assert.DoesNotContain("may have been deleted", partial);
        var filtered = await client.GetStringAsync($"/sources/{deployment}/{source}/branches?search=nonexistent");
        Assert.Contains("No remote references match", filtered); Assert.Contains("main (saved)", filtered);
        dispatcher.Output = "[\"refs/heads/other\"]";
        var missing = await client.GetStringAsync($"/sources/{deployment}/{source}/branches");
        Assert.Contains("may have been deleted", missing); Assert.Contains("not in listing", missing);
        dispatcher.Offline = true;
        var offline = await client.GetStringAsync($"/sources/{deployment}/{source}/branches");
        Assert.Contains("offline", offline); Assert.Contains("saved reference is unchanged", offline);
    }

    [Fact]
    public async Task Metadata_pages_do_not_expose_secrets_reveal_is_post_only_and_csrf_protected()
    {
        await using var original = new StanzaFactory();
        await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<SourceService>(); services.RemoveAll<IAgentCommandDispatcher>(); services.AddSingleton<IAgentCommandDispatcher, Dispatcher>();
        }));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Guid root, credential, deployment;
        using (var scope = factory.Services.CreateScope())
        {
            root = (await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
            credential = (await scope.ServiceProvider.GetRequiredService<SourceService>().SaveCredentialAsync(root, null, 0,
                new("Git", "git", "https://git.example.test/app", "private-username", "private-password"))).Id;
            deployment = (await scope.ServiceProvider.GetRequiredService<DeploymentDraftService>().CreateAsync(root, "App", null, "services: {}", "")).Id;
        }
        await Login(client, "owner");
        var list = await client.GetAsync("/credentials");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var metadata = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-username", metadata); Assert.DoesNotContain("private-password", metadata);
        Assert.True(list.Headers.CacheControl!.NoStore);
        var edit = await client.GetStringAsync($"/credentials/{credential}/edit");
        Assert.DoesNotContain("private-password", edit); Assert.DoesNotContain("private-username", edit);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync($"/credentials/{credential}/reveal")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/credentials/{credential}/reveal", Form())).StatusCode);
        var revealed = await client.PostAsync($"/credentials/{credential}/reveal", Form(("__RequestVerificationToken", Token(metadata))));
        Assert.Equal(HttpStatusCode.OK, revealed.StatusCode);
        Assert.Contains("private-password", await revealed.Content.ReadAsStringAsync());
        Assert.True(revealed.Headers.CacheControl!.NoStore);
        var sources = await client.GetAsync($"/sources/{deployment}");
        Assert.Equal(HttpStatusCode.OK, sources.StatusCode);
        Assert.Contains("No Git sources", await sources.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/sources/{deployment}/registries", Form())).StatusCode);
    }

    [Fact]
    public async Task Ordinary_account_cannot_reveal_or_mutate_without_grants_and_invalid_secret_is_not_reflected()
    {
        await using var original = new StanzaFactory();
        await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<SourceService>(); services.RemoveAll<IAgentCommandDispatcher>(); services.AddSingleton<IAgentCommandDispatcher, Dispatcher>();
        }));
        using var owner = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var ordinary = factory.CreateClient(new() { AllowAutoRedirect = false });
        Guid root, credential, user;
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            root = (await accounts.BootstrapAsync("owner", "a-secure-password-123")).Id;
            user = (await accounts.CreateAsync(root, "ordinary", "a-secure-password-123", AccountRole.User)).Id;
            credential = (await scope.ServiceProvider.GetRequiredService<SourceService>().SaveCredentialAsync(root, null, 0,
                new("Git", "git", "https://git.example.test/app", "private-username", "private-password"))).Id;
        }
        await Login(owner, "owner"); await Login(ordinary, "ordinary");
        var csrf = Token(await owner.GetStringAsync("/credentials/create"));
        var failed = await owner.PostAsync("/credentials/save", Form(("__RequestVerificationToken", csrf), ("Name", "invalid"),
            ("Kind", "git"), ("Endpoint", "file:///etc/passwd"), ("Username", "submitted-secret-user"), ("Password", "submitted-secret-password")));
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var html = await failed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("submitted-secret-password", html); Assert.DoesNotContain("submitted-secret-user", html);
        var userCsrf = Token(await ordinary.GetStringAsync("/"));
        var denied = await ordinary.PostAsync($"/credentials/{credential}/reveal", Form(("__RequestVerificationToken", userCsrf)));
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode); Assert.Contains("denied", denied.Headers.Location!.ToString());
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AccessService>().AddGrantAsync(root, user, null, "credential.read", "credential", credential);
        await Login(ordinary, "ordinary"); // Grant changes invalidate the old session.
        var readable = await ordinary.GetStringAsync("/credentials");
        Assert.Contains("https://git.example.test/app", readable); Assert.DoesNotContain("private-password", readable);
        denied = await ordinary.PostAsync($"/credentials/{credential}/reveal", Form(("__RequestVerificationToken", Token(readable))));
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode); Assert.Contains("denied", denied.Headers.Location!.ToString());
    }

    private static async Task Login(HttpClient client, string username)
    {
        var csrf = Token(await client.GetStringAsync("/account/login"));
        var response = await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", username), ("Password", "a-secure-password-123")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success); return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) => new(values.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));
    private sealed class Dispatcher : IAgentCommandDispatcher
    {
        public bool Offline { get; set; } = true;
        public string Output { get; set; } = "[\"refs/heads/main\",\"refs/tags/v1\"]";
        public bool IsConnected(Guid agentId) => !Offline;
        public Task<AgentWorkResponse> DispatchAsync(Guid agentId, AgentWorkRequest request, CancellationToken token = default)
        {
            if (Offline) throw new AgentUnavailableException();
            return Task.FromResult(new AgentWorkResponse(request.Id, true, "ok", Output));
        }
    }
}
