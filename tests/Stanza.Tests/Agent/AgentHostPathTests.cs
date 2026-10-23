using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;
using Stanza.Tests.ImageProbe;

namespace Stanza.Tests.Agent;

public sealed class AgentHostPathTests
{
    private const string Root = "/var/lib/stanza-agent";
    private const string Workspace = Root + "/operation-workspaces/work";

    [Theory]
    [InlineData(Workspace + "/assets/a", "/srv/one/agent_data/operation-workspaces/work/assets/a")]
    [InlineData("./assets/a", "/srv/one/agent_data/operation-workspaces/work/assets/a")]
    [InlineData(Root, "/srv/one/agent_data")]
    [InlineData(Root + "-other/a", Root + "-other/a")]
    [InlineData("/srv/external/data", "/srv/external/data")]
    public void Only_paths_inside_the_actual_Agent_root_are_translated(string source, string expected) =>
        Assert.Equal(expected, new AgentHostPathMap(Root, "/srv/one/agent_data").Translate(source, Workspace));

    [Theory]
    [InlineData("bind")]
    [InlineData("volume")]
    public void Writable_self_mounts_resolve_the_daemon_host_path(string type)
    {
        var map = DockerAgentHostPathResolver.FromMounts(Root, Mounts(type: type));
        Assert.Equal(Root, map.AgentRoot); Assert.Equal("/srv/one/agent_data", map.HostRoot);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"Destination\":\"/elsewhere\",\"Source\":\"/srv/data\",\"Type\":\"bind\",\"RW\":true}]")]
    [InlineData("[{\"Destination\":\"/var/lib/stanza-agent\",\"Source\":\"relative\",\"Type\":\"bind\",\"RW\":true}]")]
    [InlineData("[{\"Destination\":\"/var/lib/stanza-agent\",\"Source\":\"/\",\"Type\":\"bind\",\"RW\":true}]")]
    [InlineData("[{\"Destination\":\"/var/lib/stanza-agent\",\"Source\":\"/srv/data\",\"Type\":\"tmpfs\",\"RW\":true}]")]
    [InlineData("[{\"Destination\":\"/var/lib/stanza-agent\",\"Source\":\"/srv/data\",\"Type\":\"bind\",\"RW\":false}]")]
    [InlineData("[{\"Destination\":\"/var/lib/stanza-agent/submount\",\"Source\":\"/srv/data\",\"Type\":\"bind\",\"RW\":true}]")]
    public void Unusable_or_shadowing_mount_metadata_is_refused(string mounts) =>
        Assert.Throws<IOException>(() => DockerAgentHostPathResolver.FromMounts(Root, mounts));

    [Fact]
    public void Ambiguous_mounts_are_refused()
    {
        var item = JsonSerializer.Deserialize<JsonElement[]>(Mounts())![0];
        Assert.Throws<IOException>(() => DockerAgentHostPathResolver.FromMounts(Root, JsonSerializer.Serialize(new[] { item, item })));
    }

    [Fact]
    public async Task Discovery_inspects_only_the_Agent_container_using_the_selected_clean_Docker_environment()
    {
        using var directory = new AgentTestDirectory();
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = _ => new(0, Mounts(directory.Path), "", false, false) };
        var environment = new Dictionary<string, string> { ["DOCKER_HOST"] = "unix:///selected.sock", ["PATH"] = "/usr/bin" };
        var resolver = new DockerAgentHostPathResolver(runner, Options.Create(new DockerRuntimeOptions()), "self-container");
        var map = await resolver.ResolveAsync(directory.Path, environment, default);
        Assert.Equal("/srv/one/agent_data", map.HostRoot);
        var process = Assert.Single(runner.Requests);
        Assert.Equal(new[] { "inspect", "--type", "container", "--format", "{{json .Mounts}}", "--", "self-container" }, process.Arguments);
        Assert.Same(environment, process.Environment); Assert.Equal(directory.Path, process.WorkingDirectory);
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, true)]
    public async Task Failed_or_unbounded_mount_discovery_is_refused(int exit, bool timedOut, bool outputLimit)
    {
        using var directory = new AgentTestDirectory();
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = _ => new(exit, Mounts(directory.Path), "", outputLimit, timedOut) };
        var resolver = new DockerAgentHostPathResolver(runner, Options.Create(new DockerRuntimeOptions()), "self-container");
        await Assert.ThrowsAsync<IOException>(() => resolver.ResolveAsync(directory.Path, new Dictionary<string, string>(), default));
    }

    [Fact]
    public void Overrides_preserve_bind_options_and_ignore_named_volumes_external_mounts_and_build_contexts()
    {
        using var document = Configuration(Workspace);
        var paths = new AgentHostPathMap(Root, "/srv/one/agent_data");
        Assert.True(AgentHostPathOverrides.NeedsResolution(document.RootElement, Workspace, Root));
        var overrides = AgentHostPathOverrides.Create(document.RootElement, Workspace, paths);
        var mount = Assert.Single(overrides["services"]!["app"]!["volumes"]!.AsArray())!;
        Assert.Equal("/srv/one/agent_data/operation-workspaces/work/assets", mount["source"]!.GetValue<string>());
        Assert.True(mount["read_only"]!.GetValue<bool>());
        Assert.Equal("rprivate", mount["bind"]!["propagation"]!.GetValue<string>());
        Assert.Null(overrides["services"]!["app"]!["build"]);
        Assert.Equal("/srv/one/agent_data/operation-workspaces/work/app.conf", overrides["configs"]!["app_config"]!["file"]!.GetValue<string>());
        Assert.Equal("/srv/one/agent_data/operation-workspaces/work/key.txt", overrides["secrets"]!["app_secret"]!["file"]!.GetValue<string>());
        Assert.DoesNotContain("/srv/external", overrides.ToJsonString()); Assert.DoesNotContain("named_data", overrides.ToJsonString());
        Assert.Equal(Workspace, document.RootElement.GetProperty("services").GetProperty("app").GetProperty("build").GetProperty("context").GetString());
        var other = AgentHostPathOverrides.Create(document.RootElement, Workspace, new(Root, "/srv/two/agent_data"));
        Assert.Contains("/srv/two/agent_data", other.ToJsonString()); Assert.DoesNotContain("/srv/one/agent_data", other.ToJsonString());
    }

    [Fact]
    public void Generated_override_escapes_literal_dollars_before_Compose_reads_it_again()
    {
        var value = new JsonObject { ["path"] = "/srv/$folder/agent_data" };
        Assert.Contains("$$folder", AgentHostPathOverrides.Serialize(value));
        Assert.Equal("/srv/$folder/agent_data", value["path"]!.GetValue<string>());
    }

    [Fact]
    public void Canonical_dollar_paths_and_targets_are_decoded_once_before_the_override_is_escaped()
    {
        using var configuration = JsonDocument.Parse("{\"services\":{\"app\":{\"volumes\":[{\"type\":\"bind\",\"source\":\"/var/lib/stanza-agent/data/$$folder\",\"target\":\"/$$folder\"}]}}}");
        var overrides = AgentHostPathOverrides.Create(configuration.RootElement, Workspace, new(Root, "/srv/agent_data"));
        var mount = Assert.Single(overrides["services"]!["app"]!["volumes"]!.AsArray())!;
        Assert.Equal("/srv/agent_data/data/$folder", mount["source"]!.GetValue<string>());
        Assert.Equal("/$folder", mount["target"]!.GetValue<string>());
        Assert.DoesNotContain("$$$$", AgentHostPathOverrides.Serialize(overrides));
    }

    [Fact]
    public async Task Deploy_uses_private_temporary_host_override_only_after_build_pull_without_rewriting_authored_files()
    {
        using var directory = new AgentTestDirectory(); var images = new ImageProbeRunner();
        var request = images.Request("deploy");
        var workspace = Path.Combine(directory.Path, "operation-workspaces", request.Id.ToString("N"));
        using var configuration = Configuration(workspace);
        images.ConfigJson = configuration.RootElement.GetRawText();
        var original = request.Files[0].Content.ToArray();
        string? captured = null; string? overridePath = null;
        var runner = new ObservingRunner(images, process =>
        {
            if (!process.Arguments.Contains("up"))
            {
                if (process.Arguments.Contains("build") || process.Arguments.Contains("pull") || process.Arguments.Contains("config"))
                    Assert.DoesNotContain(process.Arguments, arg => arg.EndsWith("host-paths.json", StringComparison.Ordinal));
                return;
            }
            overridePath = Path.Combine(process.Environment["DOCKER_CONFIG"], "host-paths.json");
            Assert.Contains(overridePath, process.Arguments); Assert.False(overridePath.StartsWith(workspace + "/", StringComparison.Ordinal));
            captured = File.ReadAllText(overridePath);
        });
        var resolver = new FixedResolver(directory.Path, "/srv/installation/agent_data");
        var executor = new AgentWorkExecutor(runner, Options.Create(new AgentOptions { PersistentDataRoot = directory.Path }),
            Options.Create(new DockerRuntimeOptions()), Options.Create(new HostExecutionOptions()), resolver);
        var response = await executor.ExecuteAsync(request);
        Assert.True(response.Succeeded, response.Code); Assert.Equal(1, resolver.Calls);
        Assert.NotNull(captured); Assert.Contains("/srv/installation/agent_data/operation-workspaces/", captured);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(workspace, "compose.yaml")));
        Assert.False(File.Exists(overridePath));
    }

    [Fact]
    public async Task Required_mapping_failure_prevents_applying_a_workload()
    {
        using var directory = new AgentTestDirectory(); var images = new ImageProbeRunner(); var request = images.Request("deploy");
        var workspace = Path.Combine(directory.Path, "operation-workspaces", request.Id.ToString("N"));
        using var configuration = Configuration(workspace); images.ConfigJson = configuration.RootElement.GetRawText();
        var executor = new AgentWorkExecutor(images, Options.Create(new AgentOptions { PersistentDataRoot = directory.Path }),
            Options.Create(new DockerRuntimeOptions()), Options.Create(new HostExecutionOptions()), new FailingResolver());
        var result = await executor.ExecuteAsync(request);
        Assert.False(result.Succeeded); Assert.Equal("agent_data_mount_unavailable", result.Code);
        Assert.DoesNotContain(images.Requests, process => process.Arguments.Contains("up"));
    }

    [Fact]
    public async Task Image_only_workloads_do_not_need_self_mount_discovery()
    {
        using var directory = new AgentTestDirectory();
        var images = new ImageProbeRunner { ConfigJson = "{\"services\":{\"app\":{\"image\":\"registry.example/app:latest\"}}}" };
        var request = images.Request("deploy");
        var executor = new AgentWorkExecutor(images, Options.Create(new AgentOptions { PersistentDataRoot = directory.Path }),
            Options.Create(new DockerRuntimeOptions()), Options.Create(new HostExecutionOptions()), new FailingResolver());
        var response = await executor.ExecuteAsync(request);
        Assert.True(response.Succeeded, response.Code);
        Assert.DoesNotContain(images.Requests, process => process.Arguments.Contains("{{json .Mounts}}"));
    }

    private static string Mounts(string root = Root, string type = "bind") => JsonSerializer.Serialize(new[]
    { new { Destination = root, Source = "/srv/one/agent_data", Type = type, RW = true } });

    private static JsonDocument Configuration(string workspace) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        services = new { app = new { image = "registry.example/app:latest", build = new { context = workspace }, volumes = new object[]
        {
            new { type = "bind", source = workspace + "/assets", target = "/assets", read_only = true, bind = new { propagation = "rprivate" } },
            new { type = "bind", source = "/srv/external", target = "/external" },
            new { type = "volume", source = "named_data", target = "/data" }
        } } },
        configs = new { app_config = new { file = workspace + "/app.conf" } },
        secrets = new { app_secret = new { file = workspace + "/key.txt" } }
    }));

    private sealed class FixedResolver(string root, string host) : IAgentHostPathResolver
    {
        public int Calls { get; private set; }
        public Task<AgentHostPathMap> ResolveAsync(string dataRoot, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
        { Calls++; Assert.Equal(root, dataRoot); return Task.FromResult(new AgentHostPathMap(root, host)); }
    }
    private sealed class FailingResolver : IAgentHostPathResolver
    {
        public Task<AgentHostPathMap> ResolveAsync(string dataRoot, IReadOnlyDictionary<string, string> environment, CancellationToken ct) =>
            throw new IOException("No Agent mount metadata.");
    }
    private sealed class ObservingRunner(IProcessRunner inner, Action<ProcessRequest> inspect) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        { inspect(request); return inner.RunAsync(request, cancellationToken); }
    }
}
