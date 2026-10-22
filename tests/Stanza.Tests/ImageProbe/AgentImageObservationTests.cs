using System.Text.Json;
using Stanza.ControlPlane.Automation;
using Stanza.Core.Contracts;
using Stanza.Tests.Agent;

namespace Stanza.Tests.ImageProbe;

public sealed class AgentImageObservationTests
{
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ActualContainerConfigDigestIsComparedWithSelectedRemoteManifestConfigDigest(bool changed)
    {
        using var directory = new AgentTestDirectory(); var runner = new ImageProbeRunner();
        if (!changed) runner.Remote = runner.Current;
        var request = runner.Request();
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.True(response.Succeeded, response.Code);
        var observations = JsonSerializer.Deserialize<AgentImageObservation[]>(response.Output!, JsonSerializerOptions.Web)!;
        var app = Assert.Single(observations, o => o.Service == "app");
        Assert.Equal("linux/arm64/v8", app.Platform); Assert.Equal(runner.Current, app.CurrentDigest); Assert.Equal(runner.Remote, app.RemoteDigest);
        Assert.NotEqual(runner.Manifest, app.RemoteDigest); Assert.Equal(runner.Manifest, response.ResolvedImages!["app"]);
        var built = Assert.Single(observations, o => o.Service == "built"); Assert.True(built.LocallyBuilt); Assert.False(built.ExplicitRegistrySource);
        var result = observations.Select(o => new ImageDigestObservation(o.Service, o.Image, o.Platform, o.CurrentDigest, o.RemoteDigest, o.LocallyBuilt, o.ExplicitRegistrySource)).ToArray();
        Assert.Equal(changed ? 1 : 0, ImageUpdates.Changed(result, ["app", "built"]).Count);
        Assert.Single(runner.Requests, x => x.Arguments.Contains("--raw"));
        Assert.Contains(runner.Requests, x => x.Arguments.Contains("--raw") && x.Arguments[^1].EndsWith("@" + runner.Manifest, StringComparison.Ordinal));
        Assert.Contains(runner.Requests, x => x.Arguments[0] == "ps" && x.Arguments.Contains("label=com.docker.compose.project=stanza-" + request.DeploymentId.ToString("N")));
    }

    [Fact]
    public async Task UninstalledServiceHasNoBaselineAndCannotTriggerAutomaticDeployment()
    {
        using var directory = new AgentTestDirectory(); var runner = new ImageProbeRunner { Installed = false };
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.True(response.Succeeded, response.Code);
        var app = JsonSerializer.Deserialize<AgentImageObservation[]>(response.Output!, JsonSerializerOptions.Web)!.Single(o => o.Service == "app");
        Assert.Null(app.CurrentDigest);
        Assert.Empty(ImageUpdates.Changed([new(app.Service, app.Image, app.Platform, app.CurrentDigest, app.RemoteDigest)], ["app"]));
    }

    [Fact]
    public async Task ContainerdManifestImageIdIsResolvedToInstalledImmutableConfigInsteadOfComparedAsConfig()
    {
        using var directory = new AgentTestDirectory(); var runner = new ImageProbeRunner { ContainerdIdentity = true };
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.True(response.Succeeded, response.Code);
        var app = JsonSerializer.Deserialize<AgentImageObservation[]>(response.Output!, JsonSerializerOptions.Web)!.Single(o => o.Service == "app");
        Assert.Equal(runner.InstalledConfig, app.CurrentDigest); Assert.NotEqual(runner.Current, app.CurrentDigest);
        Assert.Equal(runner.Remote, app.RemoteDigest);
        Assert.Contains(runner.Requests, r => r.Arguments.Contains("--raw") && r.Arguments[^1].EndsWith("@" + runner.Current, StringComparison.Ordinal));
        Assert.Contains(runner.Requests, r => r.Arguments[0] == "image" && r.Arguments.Contains("--platform"));
    }

    [Theory]
    [InlineData("scope", "container_scope_denied")][InlineData("replicas", "installed_image_ambiguous")]
    [InlineData("platform", "image_platform_mismatch")][InlineData("ambiguous", "image_platform_unavailable")]
    [InlineData("raw", "invalid_image_manifest")][InlineData("digest", "invalid_image_digest")]
    public async Task UnknownOrMismatchedRuntimeIdentityFailsClosed(string problem, string code)
    {
        using var directory = new AgentTestDirectory(); var runner = new ImageProbeRunner();
        switch (problem)
        {
            case "scope": runner.WrongScope = true; break;
            case "replicas": runner.MixedReplicas = true; break;
            case "platform": runner.Architecture = "amd64"; break;
            case "ambiguous": runner.AmbiguousVariant = true; runner.ConfigJson = "{\"services\":{\"app\":{\"image\":\"registry.example/app\",\"platform\":\"linux/arm64\"}}}"; break;
            case "raw": runner.RawOverride = "{}"; break;
            case "digest": runner.Remote = "latest"; break;
        }
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.False(response.Succeeded); Assert.Equal(code, response.Code); Assert.Null(response.Output);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task PrivateRegistryUsesPrivateTemporaryDockerConfigAndCleansUpOnSuccessOrFailure(bool fail)
    {
        using var directory = new AgentTestDirectory(); var runner = new ImageProbeRunner { FailRegistry = fail };
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.Equal(!fail, response.Succeeded); Assert.True(runner.CredentialSeen);
        Assert.All(runner.Requests, p => { Assert.DoesNotContain("private-password", string.Join(' ', p.Arguments)); Assert.False(Directory.Exists(p.Environment["DOCKER_CONFIG"])); });
        Assert.DoesNotContain("private-password", response.ToString()); Assert.DoesNotContain("secret-registry-response", response.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(directory.Path, "credentials")));
    }

    [Fact]
    public async Task DeployPinsInternalOverrideAndRecordsActualAppliedIdentitiesWithoutRewritingCompose()
    {
        using var directory = new AgentTestDirectory(); var runner = new ImageProbeRunner { ConfigJson = "{\"services\":{\"app\":{\"image\":\"registry.example/app:latest\"}}}" };
        var request = runner.Request("deploy"); var original = request.Files[0].Content.ToArray();
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.True(response.Succeeded, response.Code); Assert.Equal(runner.Current, response.ResolvedImages!["app:config"]); Assert.Equal(runner.Manifest, response.ResolvedImages["app"]);
        var up = Assert.Single(runner.Requests, r => r.Arguments.Contains("up"));
        Assert.Contains(Path.Combine(up.WorkingDirectory, ".stanza", "pinned-images.json"), up.Arguments);
        using var pin = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(up.WorkingDirectory, ".stanza", "pinned-images.json")));
        Assert.Equal(runner.Current, pin.RootElement.GetProperty("services").GetProperty("app").GetProperty("image").GetString());
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(up.WorkingDirectory, "compose.yaml")));
        Assert.Contains("never", up.Arguments); Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("--volumes"));
        var replay = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.Equal(response.ResolvedImages["app:config"], replay.ResolvedImages!["app:config"]);
    }
}
