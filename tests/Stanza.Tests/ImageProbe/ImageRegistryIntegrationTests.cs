using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;
using Stanza.Tests.Agent;

namespace Stanza.Tests.ImageProbe;

public sealed class ImageRegistryIntegrationTests
{
    [DockerIntegrationFact, Trait("Category", "DockerIntegration")]
    public async Task AuthenticatedLocalRegistryDetectsRealInstalledConfigChangesAndRecordsReproducibleDeployments()
    {
        using var root = new AgentTestDirectory();
        var runner = new ProcessRunner(TimeProvider.System); var id = Guid.NewGuid();
        var registry = "stanza-image-probe-registry-" + id.ToString("N");
        var registryVolume = registry + "-data";
        var seedOne = "stanza-image-probe-" + id.ToString("N") + ":one";
        var seedTwo = "stanza-image-probe-" + id.ToString("N") + ":two";
        var project = "stanza-" + id.ToString("N");
        var authDirectory = Path.Combine(root.Path, "fixture-auth"); Directory.CreateDirectory(authDirectory);
        var buildDirectory = Path.Combine(root.Path, "fixture-build"); Directory.CreateDirectory(buildDirectory);
        var environment = new Dictionary<string, string> { ["PATH"] = "/usr/local/bin:/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock", ["DOCKER_CONFIG"] = authDirectory };
        string? image = null;
        async Task<string> Docker(params string[] arguments)
        {
            var result = await runner.RunAsync(new("/usr/bin/docker", root.Path, arguments, environment, TimeSpan.FromSeconds(120), 1048576));
            Assert.True(result.ExitCode == 0 && !result.TimedOut && !result.OutputLimitExceeded, "Fixture Docker command failed: " + string.Join(' ', arguments.Take(3)) + "\n" + result.StandardError);
            return result.StandardOutput.Trim();
        }
        var executor = new AgentWorkExecutor(runner, Options.Create(new AgentOptions { PersistentDataRoot = Path.Combine(root.Path, "agent") }),
            Options.Create(new DockerRuntimeOptions()), Options.Create(new HostExecutionOptions()));
        AgentWorkRequest Request(string operation, string registryAddress) => new(Guid.NewGuid(), id, operation,
            [new("compose.yaml", Encoding.UTF8.GetBytes($"services:\n  app:\n    image: {image}\n    command: [sleep, '300']\n    healthcheck:\n      test: [CMD, /bin/sh, -c, 'test -e /etc/alpine-release']\n      interval: 1s\n      timeout: 1s\n      retries: 10\n"))],
            [new("global.env", ""), new("agent.env", ""), new("deployment.env", "")],
            Registries: [new(registryAddress, "fixture-user", "stanza-local-fixture-password")], Policy: new(TimeoutSeconds: 120, MaximumOutputCharacters: 1048576));
        try
        {
            // These credentials exist only in the isolated test registry; no external provider is used.
            var htpasswd = Path.Combine(root.Path, "htpasswd");
            await File.WriteAllTextAsync(htpasswd, "fixture-user:$2y$05$abcdefghijklmnopqrstuu/YYaYiGhIYxTB2O.ql.pB84yhfAJGvS\n");
            await Docker("volume", "create", "--label", "io.stanza.fixture=" + id.ToString("N"), registryVolume);
            await Docker("create", "--name", registry, "--mount", "type=volume,source=" + registryVolume + ",target=/var/lib/registry", "--publish", "127.0.0.1::5000", "--env", "REGISTRY_AUTH=htpasswd", "--env", "REGISTRY_AUTH_HTPASSWD_REALM=fixture", "--env", "REGISTRY_AUTH_HTPASSWD_PATH=/htpasswd", "registry:2");
            await Docker("cp", htpasswd, registry + ":/htpasswd"); await Docker("start", registry);
            var address = await Docker("port", registry, "5000/tcp");
            using var client = new HttpClient();
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                try { using var response = await client.GetAsync("http://" + address + "/v2/", ready.Token); if (response.StatusCode == HttpStatusCode.Unauthorized) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100, ready.Token);
            }
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-user:stanza-local-fixture-password"));
            await File.WriteAllTextAsync(Path.Combine(authDirectory, "config.json"), JsonSerializer.Serialize(new { auths = new Dictionary<string, object> { [address] = new { auth } } }));
            image = address + "/stanza-fixture/app:latest";
            var dockerfile = Path.Combine(buildDirectory, "Dockerfile");
            await File.WriteAllTextAsync(dockerfile, "FROM alpine:3.22\nLABEL io.stanza.fixture.version=one\n");
            await Docker("build", "--tag", seedOne, buildDirectory); await Docker("tag", seedOne, image); await Docker("push", image);
            var first = await executor.ExecuteAsync(Request("deploy", address)); Assert.True(first.Succeeded, first.Code);
            Assert.True(first.ResolvedImages!.ContainsKey("app:config")); Assert.True(first.ResolvedImages.ContainsKey("app"));
            var unauthorized = await executor.ExecuteAsync(Request("image-digests", address) with { Registries = [new(address, "fixture-user", "wrong-fixture-password")] });
            Assert.False(unauthorized.Succeeded); Assert.Null(unauthorized.Output);
            Assert.DoesNotContain("wrong-fixture-password", unauthorized.ToString());
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root.Path, "agent", "credentials")));
            var initial = await executor.ExecuteAsync(Request("image-digests", address)); Assert.True(initial.Succeeded, initial.Code);
            var baseline = Assert.Single(JsonSerializer.Deserialize<AgentImageObservation[]>(initial.Output!, JsonSerializerOptions.Web)!);
            Assert.Equal(baseline.CurrentDigest, baseline.RemoteDigest); Assert.Equal(first.ResolvedImages["app:config"], baseline.CurrentDigest);
            await File.WriteAllTextAsync(dockerfile, "FROM alpine:3.22\nLABEL io.stanza.fixture.version=two\n");
            await Docker("build", "--tag", seedTwo, buildDirectory); await Docker("tag", seedTwo, image); await Docker("push", image);
            // The local tag now points at v2 too; only inspection of the actual v1 container is a valid baseline.
            var changed = await executor.ExecuteAsync(Request("image-digests", address)); Assert.True(changed.Succeeded, changed.Code);
            var available = Assert.Single(JsonSerializer.Deserialize<AgentImageObservation[]>(changed.Output!, JsonSerializerOptions.Web)!);
            Assert.Equal(baseline.CurrentDigest, available.CurrentDigest); Assert.NotEqual(available.CurrentDigest, available.RemoteDigest);
            Assert.NotEqual(changed.ResolvedImages!["app"], available.RemoteDigest); // manifest identity != config identity
            var redeployed = await executor.ExecuteAsync(Request("deploy", address)); Assert.True(redeployed.Succeeded, redeployed.Code);
            Assert.Equal(available.RemoteDigest, redeployed.ResolvedImages!["app:config"]);
            var final = await executor.ExecuteAsync(Request("image-digests", address)); Assert.True(final.Succeeded, final.Code);
            var applied = Assert.Single(JsonSerializer.Deserialize<AgentImageObservation[]>(final.Output!, JsonSerializerOptions.Web)!);
            Assert.Equal(applied.CurrentDigest, applied.RemoteDigest); Assert.Equal(available.RemoteDigest, applied.CurrentDigest);
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root.Path, "agent", "credentials")));
        }
        finally
        {
            // Only explicitly test-owned containers/images are removed; never prune or delete workload volumes.
            var containers = await runner.RunAsync(new("/usr/bin/docker", root.Path, ["ps", "--all", "--quiet", "--filter", "label=com.docker.compose.project=" + project], environment, TimeSpan.FromSeconds(30)));
            foreach (var container in containers.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await runner.RunAsync(new("/usr/bin/docker", root.Path, ["rm", "--force", "--", container], environment, TimeSpan.FromSeconds(30)));
            await runner.RunAsync(new("/usr/bin/docker", root.Path, ["rm", "--force", "--", registry], environment, TimeSpan.FromSeconds(30)));
            await runner.RunAsync(new("/usr/bin/docker", root.Path, ["volume", "rm", "--", registryVolume], environment, TimeSpan.FromSeconds(30)));
            foreach (var tag in new[] { image, seedOne, seedTwo }.Where(x => x is not null))
                await runner.RunAsync(new("/usr/bin/docker", root.Path, ["image", "rm", "--", tag!], environment, TimeSpan.FromSeconds(30)));
            var networks = await runner.RunAsync(new("/usr/bin/docker", root.Path, ["network", "ls", "--quiet", "--filter", "label=com.docker.compose.project=" + project], environment, TimeSpan.FromSeconds(30)));
            foreach (var network in networks.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await runner.RunAsync(new("/usr/bin/docker", root.Path, ["network", "rm", "--", network], environment, TimeSpan.FromSeconds(30)));
        }
    }
}
