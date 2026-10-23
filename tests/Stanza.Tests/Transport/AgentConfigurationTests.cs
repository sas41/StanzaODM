using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Transport;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Tests.Agent;
using Stanza.Tests.Operations;

namespace Stanza.Tests.Transport;

public sealed class AgentConfigurationTests
{
    [Fact]
    public void Invalid_agent_identity_duplicate_assignments_and_unbounded_policy_are_rejected()
    {
        using var directory = new AgentTestDirectory(); var id = Guid.NewGuid();
        Assert.Throws<IOException>(() => Cache(directory.Path).Begin(Configuration(id) with { AgentId = Guid.Empty }));
        Assert.Throws<IOException>(() => Cache(directory.Path).Begin(Configuration(id, id)));
        Assert.Throws<IOException>(() => Cache(directory.Path).Begin(Configuration(id) with { Policy = new(TimeoutSeconds: -1) }));
        Assert.Throws<IOException>(() => Cache(directory.Path).Begin(Configuration(id) with { AgentEnvironment = "bad\0value" }));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "configuration-cache")));
    }

    private static AgentDeploymentConfiguration Deployment(Guid? id = null) => new(id ?? Guid.NewGuid(), "App", 1,
        [new("compose.yaml", Encoding.UTF8.GetBytes("# preserved\r\nservices: {}\r\n")),
         new("assets/logo.bin", [0, 255, 1]), new("empty/nested", [], true)],
        "SECRET='deployment value'\n", [new("https://git.example/app", "main", "sources/app", Guid.NewGuid())], []);
    private static AgentConfiguration Configuration(params Guid[] ids) => new(Guid.NewGuid(), "SECRET=global\n", "SECRET=agent\n", new(), ids);
    private static AgentConfigurationCache Cache(string root) => new(Options.Create(new AgentOptions { PersistentDataRoot = root }));

    [Fact]
    public async Task Full_sync_persists_assigned_identity_policy_exact_files_and_environment_without_touching_runtime()
    {
        using var directory = new AgentTestDirectory();
        var untouched = Path.Combine(directory.Path, "operation-workspaces", "running");
        Directory.CreateDirectory(untouched); await File.WriteAllTextAsync(Path.Combine(untouched, "keep"), "running");
        var deployment = Deployment(); var config = Configuration(deployment.Id) with { Policy = new(false, 42) };
        var cache = Cache(directory.Path); cache.Begin(config); await cache.ApplyAsync(deployment, default); await cache.CompleteAsync(default);
        var files = Path.Combine(cache.Root, "deployments", deployment.Id.ToString("N"), "files");
        Assert.Equal(deployment.Files[0].Content, await File.ReadAllBytesAsync(Path.Combine(files, "compose.yaml")));
        Assert.Equal(new byte[] { 0, 255, 1 }, await File.ReadAllBytesAsync(Path.Combine(files, "assets/logo.bin")));
        Assert.True(Directory.Exists(Path.Combine(files, "empty/nested")));
        Assert.Equal(deployment.Environment, await File.ReadAllTextAsync(Path.Combine(files, ".env")));
        Assert.Equal(config.GlobalEnvironment, await File.ReadAllTextAsync(Path.Combine(cache.Root, "global.env")));
        Assert.Equal(config.AgentEnvironment, await File.ReadAllTextAsync(Path.Combine(cache.Root, "agent.env")));
        var saved = JsonSerializer.Deserialize<AgentConfiguration>(await File.ReadAllTextAsync(Path.Combine(cache.Root, "agent.json")), JsonSerializerOptions.Web)!;
        Assert.Equal(config.AgentId, saved.AgentId); Assert.Equal(config.Policy, saved.Policy);
        Assert.Equal("running", await File.ReadAllTextAsync(Path.Combine(untouched, "keep")));
        if (!OperatingSystem.IsWindows()) Assert.Equal(PrivateStorage.FileMode, File.GetUnixFileMode(Path.Combine(files, ".env")));
        Assert.DoesNotContain("deployment value", deployment.ToString()); Assert.DoesNotContain("SECRET", config.ToString());
    }

    [Fact]
    public async Task Changed_files_replace_the_cache_and_removed_assignments_delete_only_cache_contents()
    {
        using var directory = new AgentTestDirectory(); var cache = Cache(directory.Path);
        var deployment = Deployment(); var config = Configuration(deployment.Id);
        cache.Begin(config); await cache.ApplyAsync(deployment, default); await cache.CompleteAsync(default);
        cache.Begin(config); await cache.ApplyAsync(deployment with { Revision = 2, Files = [deployment.Files[0]] }, default); await cache.CompleteAsync(default);
        var target = Path.Combine(cache.Root, "deployments", deployment.Id.ToString("N"));
        Assert.False(File.Exists(Path.Combine(target, "files/assets/logo.bin")));
        cache.Begin(config with { DeploymentIds = [] }); await cache.CompleteAsync(default);
        Assert.False(Directory.Exists(target)); Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(cache.Root, "deployments")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData(".git/config")]
    [InlineData(".env")]
    [InlineData("a\\b")]
    public async Task Invalid_file_paths_never_replace_a_valid_cache(string path)
    {
        using var directory = new AgentTestDirectory(); var cache = Cache(directory.Path); var deployment = Deployment(); var config = Configuration(deployment.Id);
        cache.Begin(config); await cache.ApplyAsync(deployment, default); await cache.CompleteAsync(default);
        cache.Begin(config);
        await Assert.ThrowsAnyAsync<Exception>(() => cache.ApplyAsync(deployment with { Files = [deployment.Files[0], new(path, [1])] }, default));
        Assert.Equal(deployment.Files[0].Content, await File.ReadAllBytesAsync(Path.Combine(cache.Root, "deployments", deployment.Id.ToString("N"), "files/compose.yaml")));
    }

    [Fact]
    public async Task Unexpected_assignment_incomplete_sync_and_case_aliases_are_rejected()
    {
        using var directory = new AgentTestDirectory(); var cache = Cache(directory.Path); var deployment = Deployment();
        cache.Begin(Configuration(deployment.Id));
        await Assert.ThrowsAsync<IOException>(() => cache.ApplyAsync(Deployment(), default));
        await Assert.ThrowsAsync<IOException>(() => cache.CompleteAsync(default));
        await Assert.ThrowsAsync<IOException>(() => cache.ApplyAsync(deployment with
        { Files = [deployment.Files[0], new("assets/a", [1]), new("Assets/b", [2])] }, default));
        Assert.False(File.Exists(Path.Combine(cache.Root, "agent.json")));
    }

    [Fact]
    public async Task Symlink_in_cache_is_not_followed_or_deleted()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new AgentTestDirectory(); using var outside = new AgentTestDirectory();
        var cache = Cache(directory.Path); var deployment = Deployment(); cache.Begin(Configuration(deployment.Id));
        var target = Path.Combine(cache.Root, "deployments", deployment.Id.ToString("N"));
        Directory.CreateSymbolicLink(target, outside.Path);
        await Assert.ThrowsAsync<IOException>(() => cache.ApplyAsync(deployment, default));
        Assert.True(Directory.Exists(outside.Path));
        File.Delete(target);
    }

    [Fact]
    public async Task Cancelled_write_keeps_previous_complete_deployment()
    {
        using var directory = new AgentTestDirectory(); var cache = Cache(directory.Path); var deployment = Deployment(); var config = Configuration(deployment.Id);
        cache.Begin(config); await cache.ApplyAsync(deployment, default); await cache.CompleteAsync(default);
        cache.Begin(config); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ApplyAsync(deployment, cancelled.Token));
        Assert.True(File.Exists(Path.Combine(cache.Root, "deployments", deployment.Id.ToString("N"), "configuration.json")));
    }

    [Fact]
    public async Task Transfers_full_on_boot_and_reconnect_but_only_changed_deployments_afterwards()
    {
        var first = Deployment(); var second = Deployment(); var config = Configuration(first.Id, second.Id);
        var snapshot = new AgentConfigurationSnapshot(config, [first, second], 15);
        var sent = new List<AgentWireMessage>(); string? expected = null;
        Task Send(AgentWireMessage message, CancellationToken _) { sent.Add(message); return Task.CompletedTask; }
        var transfer = new AgentConfigurationTransfer();
        Assert.True(await transfer.SendAsync(snapshot, Send, value => expected = value, default));
        Assert.Equal(2, sent.Count(x => x.Type == "configuration.deployment")); Assert.Equal(expected, sent[^1].Data);
        sent.Clear(); Assert.False(await transfer.SendAsync(snapshot, Send, _ => { }, default)); Assert.Empty(sent);
        Assert.True(await transfer.SendAsync(snapshot with { Configuration = config with { GlobalEnvironment = "UPDATED=yes\n" } }, Send, _ => { }, default));
        Assert.DoesNotContain(sent, x => x.Type == "configuration.deployment"); sent.Clear();
        Assert.True(await transfer.SendAsync(snapshot with { Deployments = [first with { Revision = 2 }, second] }, Send, _ => { }, default));
        Assert.Equal(first.Id, Assert.Single(sent, x => x.Type == "configuration.deployment").Id);
        sent.Clear(); await new AgentConfigurationTransfer().SendAsync(snapshot, Send, _ => { }, default);
        Assert.Equal(2, sent.Count(x => x.Type == "configuration.deployment"));
    }

    [Fact]
    public async Task Failed_transfer_is_retried_in_full()
    {
        var deployment = Deployment(); var snapshot = new AgentConfigurationSnapshot(Configuration(deployment.Id), [deployment], 15);
        var transfer = new AgentConfigurationTransfer();
        await Assert.ThrowsAsync<IOException>(() => transfer.SendAsync(snapshot, (_, _) => throw new IOException(), _ => { }, default));
        var sent = new List<AgentWireMessage>();
        await transfer.SendAsync(snapshot, (message, _) => { sent.Add(message); return Task.CompletedTask; }, _ => { }, default);
        Assert.Single(sent, x => x.Type == "configuration.deployment");
    }

    [Fact]
    public async Task Control_plane_sync_is_assignment_scoped_current_and_excludes_source_secrets()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var other = await h.AddAgentAsync(); await h.AddDeploymentAsync(agent: other);
        var credential = await h.GitCredentialAsync(); await h.AddSourceAsync(credential.Id);
        async Task<AgentConfigurationSnapshot> Read(Guid id) => await h.InScope(services => new AgentConfigurationSync(
            services.GetRequiredService<StanzaDbContext>(), h.Protection, services.GetRequiredService<InstanceSettingsService>(),
            services.GetRequiredService<AgentPolicyService>(), services.GetRequiredService<OperationalPolicyService>()).ReadAsync(id, default));
        var snapshot = await Read(h.Agent);
        Assert.Equal(h.Deployment, Assert.Single(snapshot.Deployments).Id);
        Assert.Contains("global-secret", snapshot.Configuration.GlobalEnvironment);
        Assert.Contains("agent-secret", snapshot.Configuration.AgentEnvironment);
        Assert.Equal(credential.Id, Assert.Single(snapshot.Deployments[0].Sources).CredentialId);
        Assert.DoesNotContain("git-password-secret", JsonSerializer.Serialize(snapshot));
        await h.InScope(async services =>
        {
            await services.GetRequiredService<AgentPolicyService>().SaveAsync(h.Root, new(h.Agent, new(false, 99), 0));
            await services.GetRequiredService<AgentAdministrationService>().SaveEnvironmentAsync(h.Root, h.Agent, new("LATEST=yes\n", 0));
            await services.GetRequiredService<DeploymentDraftService>().SaveTextFileAsync(h.Root, h.Deployment, 1, "Dockerfile", "FROM scratch\n", true);
            return true;
        });
        snapshot = await Read(h.Agent); Assert.False(snapshot.Configuration.Policy.ExecutionEnabled);
        Assert.Equal("LATEST=yes\n", snapshot.Configuration.AgentEnvironment); Assert.Equal(2, snapshot.Deployments[0].Revision);
        Assert.Contains(snapshot.Deployments[0].Files, x => x.Path == "Dockerfile");
        await h.InScope(async services =>
        {
            await services.GetRequiredService<StanzaDbContext>().Set<AgentEntity>().Where(x => x.Id == h.Agent)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, "revoked")); return true;
        });
        await Assert.ThrowsAsync<IOException>(() => Read(h.Agent));
    }
}
