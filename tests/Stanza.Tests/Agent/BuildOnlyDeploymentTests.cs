using System.Text;
using System.Text.Json;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class BuildOnlyDeploymentTests
{
    [Fact]
    public async Task EachBuildOnlyServiceUsesComposesReferenceAndIsPinnedAlongsideExplicitImages()
    {
        using var directory = new AgentTestDirectory();
        var api = new Service("api", 'a'); var worker = new Service("worker", 'b');
        var database = new Service("database", 'c') { Build = false, ExplicitImage = "registry.example/database:latest" };
        var runner = new DeploymentRunner(api, worker, database);
        var request = runner.Request(); var original = request.Files[0].Content.ToArray();
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.True(response.Succeeded, response.Code);
        var resolutions = runner.Requests.Where(x => x.Arguments.Contains("--images")).ToArray();
        Assert.Equal(new[] { "api", "worker" }, resolutions.Select(x => x.Arguments[^1]));
        Assert.All(resolutions, x =>
        {
            Assert.Equal("--", x.Arguments[^2]);
            Assert.Equal("stanza-" + request.DeploymentId.ToString("N"), x.Arguments[x.Arguments.IndexOf("--project-name") + 1]);
            Assert.Equal(3, x.Arguments.Count(a => a == "--env-file"));
        });
        var up = Assert.Single(runner.Requests, x => x.Arguments.Contains("up"));
        using var pins = JsonDocument.Parse(runner.Pins!);
        foreach (var service in runner.Services)
        {
            Assert.Equal(service.ImageId, pins.RootElement.GetProperty("services").GetProperty(service.Name).GetProperty("image").GetString());
            Assert.Equal(service.ImageId, response.ResolvedImages![service.Name + ":image-id"]);
            Assert.Equal(service.ImageId, response.ResolvedImages[service.Name + ":config"]);
        }
        var pull = Assert.Single(runner.Requests, x => x.Arguments.Contains("pull"));
        Assert.Equal("database", pull.Arguments[^1]); Assert.DoesNotContain("api", pull.Arguments); Assert.DoesNotContain("worker", pull.Arguments);
        Assert.Contains("--no-build", up.Arguments); Assert.Contains("never", up.Arguments);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(up.WorkingDirectory, "compose.yaml")));
        var pinPath = Path.Combine(up.Environment["DOCKER_CONFIG"], "pinned-images.json");
        Assert.False(pinPath.StartsWith(up.WorkingDirectory + "/", StringComparison.Ordinal)); Assert.False(File.Exists(pinPath));
        var count = runner.Requests.Count;
        var replay = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.True(replay.Succeeded); Assert.Equal(count, runner.Requests.Count);
        Assert.Equal(response.ResolvedImages!.OrderBy(x => x.Key), replay.ResolvedImages!.OrderBy(x => x.Key));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("up")]
    [InlineData("logs")]
    public async Task MachineReadableImageResolutionIsCapturedRatherThanStreamedWhenServiceNamesLookLikeCommands(string name)
    {
        using var directory = new AgentTestDirectory(); var runner = new DeploymentRunner(new Service(name, 'a'));
        var executor = AgentWorkExecutorTests.Executor(directory.Path, runner);
        var output = new StringBuilder(); executor.Output += (_, chunk) => { output.Append(chunk.Text); return Task.CompletedTask; };
        var response = await executor.ExecuteAsync(runner.Request());
        Assert.True(response.Succeeded, response.Code);
        var query = Assert.Single(runner.Requests, x => x.Arguments.Contains("--images"));
        Assert.True(query.CaptureOutput);
        Assert.DoesNotContain(runner.Services[0].Reference, output.ToString());
    }

    [Fact]
    public async Task ExplicitBuildImageNamesKeepTheirExistingBehavior()
    {
        using var directory = new AgentTestDirectory(); var service = new Service("app", 'a') { ExplicitImage = "local/app:latest" };
        var runner = new DeploymentRunner(service);
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.True(response.Succeeded, response.Code);
        Assert.DoesNotContain(runner.Requests, x => x.Arguments.Contains("--images"));
        Assert.DoesNotContain(runner.Requests, x => x.Arguments.Contains("pull"));
        Assert.Contains(runner.Requests, x => x.Arguments[0] == "image" && x.Arguments[^1] == service.ExplicitImage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StaleContainersFailBeforeLookingUpTheirMissingOldImageRecord(int replicas)
    {
        using var directory = new AgentTestDirectory(); var service = new Service("app", 'a');
        service.ContainerImages = replicas == 1 ? [Sha('b')] : [service.ImageId, Sha('b')];
        var runner = new DeploymentRunner(service); runner.MissingImages.Add(Sha('b'));
        var executor = AgentWorkExecutorTests.Executor(directory.Path, runner); var output = new StringBuilder();
        executor.Output += (_, chunk) => { output.Append(chunk.Text); return Task.CompletedTask; };
        var response = await executor.ExecuteAsync(runner.Request());
        Assert.False(response.Succeeded); Assert.Equal("deployed_image_changed", response.Code); Assert.True(runner.Applied);
        Assert.DoesNotContain(runner.Requests, x => x.Arguments[0] == "image" && x.Arguments[^1] == Sha('b'));
        Assert.Contains("service 'app'", output.ToString()); Assert.Contains("expected " + service.ImageId, output.ToString());
        Assert.Contains("Changes were not rolled back", output.ToString());
    }

    [Fact]
    public async Task AllReplicasAreVerifiedAndTheRequestedPlatformIsUsedForInspections()
    {
        using var directory = new AgentTestDirectory(); var service = new Service("app", 'a') { Platform = "linux/arm64/v8" };
        service.ContainerImages = [service.ImageId, service.ImageId];
        var runner = new DeploymentRunner(service);
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.True(response.Succeeded, response.Code);
        Assert.Equal(2, runner.Requests.Count(x => x.Arguments[0] == "inspect"));
        Assert.All(runner.Requests.Where(x => x.Arguments[0] == "image"), x =>
        {
            Assert.Contains("--platform", x.Arguments); Assert.Contains(service.Platform, x.Arguments);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("one\ntwo")]
    [InlineData("one\none")]
    public async Task EmptyOrAmbiguousGeneratedReferencesFailBeforeApplying(string images)
    {
        using var directory = new AgentTestDirectory(); var runner = new DeploymentRunner(new Service("app", 'a') { ResolutionOutput = images });
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.False(response.Succeeded); Assert.Equal("invalid_compose_image", response.Code); Assert.False(runner.Applied);
        Assert.DoesNotContain(runner.Requests, x => x.Arguments[0] == "image");
    }

    [Theory]
    [InlineData("--injected")]
    [InlineData("not an image")]
    [InlineData("https://registry.example/image")]
    public async Task UnsafeGeneratedReferencesAreRejected(string reference)
    {
        using var directory = new AgentTestDirectory(); var runner = new DeploymentRunner(new Service("app", 'a') { ResolutionOutput = reference });
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.False(response.Succeeded); Assert.Equal("invalid_image", response.Code); Assert.False(runner.Applied);
    }

    [Fact]
    public async Task MissingNewImageFailsBeforeUpAndNeverUsesAnOldContainerAsTheBuildBaseline()
    {
        using var directory = new AgentTestDirectory(); var service = new Service("app", 'a');
        var runner = new DeploymentRunner(service); runner.MissingImages.Add(service.Reference);
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.False(response.Succeeded); Assert.Equal("deployment_image_unavailable", response.Code); Assert.False(runner.Applied);
        Assert.DoesNotContain(runner.Requests, x => x.Arguments[0] is "ps" or "inspect");
    }

    [Fact]
    public async Task ComposeResolutionFailuresNeverFallBackToGuessingAnImageName()
    {
        using var directory = new AgentTestDirectory(); var runner = new DeploymentRunner(new Service("app", 'a')) { ResolutionFails = true };
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.False(response.Succeeded); Assert.Equal("compose_image_resolution_failed", response.Code); Assert.False(runner.Applied);
        Assert.DoesNotContain(runner.Requests, x => x.Arguments[0] == "image");
    }

    [Theory]
    [InlineData(false, "deployment_image_unavailable")]
    [InlineData(true, "deployed_image_changed")]
    public async Task MissingOrInconsistentPostDeploymentMetadataNeverReportsSuccess(bool inconsistent, string code)
    {
        using var directory = new AgentTestDirectory(); var runner = new DeploymentRunner(new Service("app", 'a')) { MissingAppliedMetadata = !inconsistent, InconsistentAppliedMetadata = inconsistent };
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.False(response.Succeeded); Assert.Equal(code, response.Code); Assert.True(runner.Applied);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(0, null, true)]
    [InlineData(null, 0, true)]
    public async Task OnlyExplicitZeroScaleMayHaveNoContainersAfterUp(int? scale, int? replicas, bool succeeds)
    {
        using var directory = new AgentTestDirectory(); var runner = new DeploymentRunner(new Service("app", 'a') { Scale = scale, Replicas = replicas, ContainerImages = [] });
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.Equal(succeeds, response.Succeeded); Assert.True(runner.Applied);
        if (!succeeds) Assert.Equal("deployed_container_missing", response.Code);
    }

    [Fact]
    public async Task ContainerScopeChecksStillApplyBeforeAcceptingImageIdentity()
    {
        using var directory = new AgentTestDirectory(); var runner = new DeploymentRunner(new Service("app", 'a')) { WrongScope = true };
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(runner.Request());
        Assert.False(response.Succeeded); Assert.Equal("container_scope_denied", response.Code);
    }

    private static string Sha(char value) => "sha256:" + new string(value, 64);

    private sealed class Service(string name, char identity)
    {
        public string Name { get; } = name;
        public string ImageId { get; } = Sha(identity);
        public string? ExplicitImage { get; init; }
        public string Reference => ExplicitImage ?? "compose-chosen/" + Name + ":build";
        public bool Build { get; init; } = true;
        public string? ResolutionOutput { get; init; }
        public string? Platform { get; init; }
        public int? Scale { get; init; }
        public int? Replicas { get; init; }
        public string[] ContainerImages { get; set; } = [Sha(identity)];
        public Dictionary<string, object> Configuration()
        {
            var result = new Dictionary<string, object>();
            if (Build) result["build"] = new { context = "." };
            if (ExplicitImage is not null) result["image"] = ExplicitImage;
            if (Platform is not null) result["platform"] = Platform;
            if (Scale.HasValue) result["scale"] = Scale.Value;
            if (Replicas.HasValue) result["deploy"] = new { replicas = Replicas.Value };
            return result;
        }
    }

    private sealed class DeploymentRunner(params Service[] services) : IProcessRunner
    {
        public Service[] Services { get; } = services;
        public List<ProcessRequest> Requests { get; } = [];
        public HashSet<string> MissingImages { get; } = [];
        public bool Applied { get; private set; }
        public bool WrongScope { get; init; }
        public bool ResolutionFails { get; init; }
        public bool MissingAppliedMetadata { get; init; }
        public bool InconsistentAppliedMetadata { get; init; }
        public string? Pins { get; private set; }
        private Guid deployment;
        public AgentWorkRequest Request()
        {
            var request = AgentWorkExecutorTests.Request("deploy"); deployment = request.DeploymentId;
            return request with { Files = [new("compose.yaml", Encoding.UTF8.GetBytes(Configuration())), new("Dockerfile", Encoding.UTF8.GetBytes("FROM scratch\n"))] };
        }
        private string Configuration() => JsonSerializer.Serialize(new { name = "authored-project-not-the-runtime-project", services = Services.ToDictionary(x => x.Name, x => x.Configuration()) });
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var result = Result(request);
            if (request.OnOutput is not null)
            {
                if (result.StandardOutput.Length > 0) await request.OnOutput("stdout", result.StandardOutput);
                if (result.StandardError.Length > 0) await request.OnOutput("stderr", result.StandardError);
            }
            return request.CaptureOutput ? result : result with { StandardOutput = "", StandardError = "" };
        }
        private ProcessResult Result(ProcessRequest request)
        {
            var a = request.Arguments; string output = "";
            var containers = Services.SelectMany((s, i) => s.ContainerImages.Select((image, j) => new { Service = s, Image = image, Id = ((i + 1) * 1000 + j).ToString("x").PadLeft(64, '0') })).ToArray();
            if (a[0] == "compose")
            {
                if (a.Contains("config"))
                {
                    if (a.Contains("--images") && ResolutionFails) return new(1, "", "Compose could not resolve the service image.", false, false);
                    if (a.Contains("--images")) { var service = Services.Single(s => s.Name == a[^1]); output = service.ResolutionOutput ?? service.Reference + "\n"; }
                    else output = Configuration();
                }
                else if (a.Contains("up")) { Applied = true; Pins = File.ReadAllText(Path.Combine(request.Environment["DOCKER_CONFIG"], "pinned-images.json")); }
                else if (a.Contains("ps")) output = "[]";
            }
            else if (a[0] == "image")
            {
                if (MissingImages.Contains(a[^1]) || Applied && MissingAppliedMetadata) return new(1, "", "No such image: " + a[^1], false, false);
                var service = Services.First(s => s.Reference == a[^1] || s.ImageId == a[^1]);
                output = JsonSerializer.Serialize(new { Id = Applied && InconsistentAppliedMetadata ? Sha('f') : service.ImageId, Os = "linux", Architecture = service.Platform?.Split('/')[1] ?? "amd64", RepoDigests = Array.Empty<string>() });
            }
            else if (a[0] == "ps")
            {
                var name = a.Single(x => x.StartsWith("label=com.docker.compose.service=", StringComparison.Ordinal)).Split('=').Last();
                output = string.Join('\n', containers.Where(x => x.Service.Name == name).Select(x => x.Id));
            }
            else if (a[0] == "inspect")
            {
                var container = containers.Single(x => x.Id == a[^1]);
                output = JsonSerializer.Serialize(new { Image = container.Image, Config = new { Labels = new Dictionary<string, string>
                { ["com.docker.compose.project"] = WrongScope ? "another-project" : "stanza-" + deployment.ToString("N"), ["com.docker.compose.service"] = container.Service.Name, ["com.docker.compose.oneoff"] = "False" } } });
            }
            return new(0, output, "", false, false);
        }
    }
}
