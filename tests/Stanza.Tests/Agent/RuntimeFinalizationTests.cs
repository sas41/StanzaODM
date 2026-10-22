using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class RuntimeFinalizationTests
{
    private const UnixFileMode ReadableDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode ReadableFile = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    [Fact]
    public async Task InterpolationAndRegistrySecretsAreOutsideEveryOrdinaryCopyBuildContextAndAreDeleted()
    {
        using var root = new AgentTestDirectory();
        var request = AgentWorkExecutorTests.Request("build") with
        {
            Files = [new("compose.yaml", Encoding.UTF8.GetBytes("services:\n  app:\n    build: .\n")),
                new("Dockerfile", Encoding.UTF8.GetBytes("FROM scratch\nCOPY . /snapshot\n"))],
            EnvironmentFiles = [new("Global", "GLOBAL_ONLY=unique-global-secret\n"), new("Agent", "AGENT_ONLY=unique-agent-secret\n"),
                new("Deployment", "DEPLOYMENT_ONLY=authored-deployment-value\n")],
            Registries = [new("registry.example", "private-user", "unique-registry-secret")]
        };
        var inspected = false;
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = invocation =>
        {
            var files = Directory.GetFiles(invocation.WorkingDirectory, "*", SearchOption.AllDirectories);
            var content = string.Join('\n', files.Select(File.ReadAllText));
            Assert.DoesNotContain("unique-global-secret", content);
            Assert.DoesNotContain("unique-agent-secret", content);
            Assert.DoesNotContain("unique-registry-secret", content);
            Assert.Contains("authored-deployment-value", File.ReadAllText(Path.Combine(invocation.WorkingDirectory, ".env")));
            var environmentDirectory = Path.Combine(invocation.Environment["DOCKER_CONFIG"], "environment");
            Assert.Contains("unique-global-secret", File.ReadAllText(Path.Combine(environmentDirectory, "environment-0.env")));
            Assert.Contains("unique-agent-secret", File.ReadAllText(Path.Combine(environmentDirectory, "environment-1.env")));
            Assert.False(environmentDirectory.StartsWith(invocation.WorkingDirectory + "/", StringComparison.Ordinal));
            inspected = true;
            return new(0, invocation.Arguments.Contains("config") ? "{\"services\":{\"app\":{\"build\":{\"context\":\".\"}}}}" : "", "", false, false);
        }};
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code); Assert.True(inspected);
        Assert.All(runner.Requests, invocation => Assert.False(Directory.Exists(invocation.Environment["DOCKER_CONFIG"])));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("remove")]
    [InlineData("status")]
    [InlineData("logs")]
    public async Task ExistingRuntimeControlNeedsNoComposeEnvironmentOrGitAndKeepsExplicitProjectScope(string operation)
    {
        using var root = new AgentTestDirectory();
        var request = AgentWorkExecutorTests.Request(operation) with
        {
            Files = [], EnvironmentFiles = [],
            Sources = [new("ssh://revoked-unavailable.example/repository", "main", "source")]
        };
        var id = new string('a', 64);
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = invocation =>
        {
            Assert.EndsWith("docker", invocation.Executable);
            Assert.DoesNotContain("compose", invocation.Arguments);
            Assert.DoesNotContain("--env-file", invocation.Arguments);
            Assert.False(File.Exists(Path.Combine(invocation.WorkingDirectory, "compose.yaml")));
            return new(0, invocation.Arguments[0] switch
            {
                "ps" => id + "\n",
                "inspect" => ContainerInspection(id, request.DeploymentId, "running", "healthy"),
                "logs" => "workload-only-log\n",
                _ => ""
            }, "", false, false);
        }};
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code);
        var discovery = Assert.Single(runner.Requests, invocation => invocation.Arguments[0] == "ps");
        Assert.Contains("label=com.docker.compose.project=stanza-" + request.DeploymentId.ToString("N"), discovery.Arguments);
        Assert.All(runner.Requests, invocation =>
        {
            Assert.DoesNotContain("--volumes", invocation.Arguments);
            Assert.DoesNotContain("-v", invocation.Arguments);
            Assert.DoesNotContain("prune", invocation.Arguments);
        });
        if (operation == "remove")
        {
            var removal = Assert.Single(runner.Requests, invocation => invocation.Arguments[0] == "rm");
            Assert.Contains("--force", removal.Arguments); Assert.Contains(id, removal.Arguments);
        }
        else if (operation == "status")
        {
            using var output = JsonDocument.Parse(result.Output!);
            var observed = Assert.Single(output.RootElement.EnumerateArray());
            Assert.Equal("running", observed.GetProperty("State").GetString());
            Assert.Equal("healthy", observed.GetProperty("Health").GetString());
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("remove")]
    [InlineData("status")]
    [InlineData("logs")]
    public async Task WrongProjectReturnedByDiscoveryIsNeverMutatedOrRead(string operation)
    {
        using var root = new AgentTestDirectory(); var id = new string('b', 64);
        var request = AgentWorkExecutorTests.Request(operation) with { Files = [], EnvironmentFiles = [] };
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = invocation => new(0,
            invocation.Arguments[0] == "ps" ? id : ContainerInspection(id, Guid.NewGuid(), "running", "healthy"), "", false, false) };
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.False(result.Succeeded);
        Assert.DoesNotContain(runner.Requests, invocation => invocation.Arguments[0] is "start" or "stop" or "restart" or "rm" or "logs");
    }

    [Fact]
    public async Task DeploymentLogSnapshotIncludesEveryScopedContainerNotOnlyTheFirstService()
    {
        using var root = new AgentTestDirectory(); var first = new string('a', 64); var second = new string('b', 64);
        var request = AgentWorkExecutorTests.Request("logs") with { Files = [], EnvironmentFiles = [] };
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = invocation => new(0,
            invocation.Arguments[0] switch
            {
                "ps" => first + "\n" + second + "\n",
                "inspect" => ContainerInspection(invocation.Arguments[^1], request.DeploymentId, "running", "healthy"),
                "logs" => invocation.Arguments[^1] == first ? "first-service-log\n" : "second-service-log\n",
                _ => ""
            }, "", false, false) };
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code);
        Assert.Contains("first-service-log", result.Output); Assert.Contains("second-service-log", result.Output);
        Assert.Equal(2, runner.Requests.Count(invocation => invocation.Arguments[0] == "logs"));
    }

    [Fact]
    public async Task ExplicitNestedEmptyDirectoriesAndManagedFilesAreReadableWhileTheirStorageParentIsPrivate()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new AgentTestDirectory(); var runner = new AgentWorkExecutorTests.FakeRunner();
        var request = AgentWorkExecutorTests.Request() with
        {
            Files = [.. AgentWorkExecutorTests.Request().Files, new("nested/config/app.conf", Encoding.UTF8.GetBytes("readable-config\n")),
                new("empty/parent/leaf", [], IsDirectory: true)]
        };
        Assert.True((await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request)).Succeeded);
        var workspace = Assert.Single(runner.Requests).WorkingDirectory;
        foreach (var directory in new[] { "", "nested", "nested/config", "empty", "empty/parent", "empty/parent/leaf" })
            Assert.Equal(ReadableDirectory, File.GetUnixFileMode(Path.Combine(workspace, directory)));
        Assert.Equal(ReadableFile, File.GetUnixFileMode(Path.Combine(workspace, "nested/config/app.conf")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(workspace, "empty/parent/leaf")));
        Assert.Equal(PrivateStorage.DirectoryMode, File.GetUnixFileMode(Path.GetDirectoryName(workspace)!));
    }

    [Fact]
    public async Task GitSourceMayUseAnExplicitManagedDirectoryAncestorWithoutOverwritingManagedSiblings()
    {
        using var root = new AgentTestDirectory(); var commit = new string('c', 40);
        var request = AgentWorkExecutorTests.Request() with
        {
            Files = [.. AgentWorkExecutorTests.Request().Files, new("sources", [], IsDirectory: true),
                new("sources/authored.txt", Encoding.UTF8.GetBytes("keep authored file"))],
            Sources = [new("https://git.example/repo", "refs/tags/release-v1", "sources/checkout", "source-user", "source-password")]
        };
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = invocation => new(0,
            invocation.Arguments.Contains("rev-parse") ? commit : invocation.Arguments.Contains("config") ? "{\"services\":{}}" : "", "", false, false) };
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code); Assert.Equal(commit, result.ResolvedSources!["sources/checkout"]);
        var fetch = Assert.Single(runner.Requests, invocation => invocation.Arguments.Contains("fetch"));
        Assert.Equal("refs/tags/release-v1", fetch.Arguments[^1]);
        Assert.Equal("source-user", fetch.Environment["STANZA_GIT_USERNAME"]);
        Assert.Equal("source-password", fetch.Environment["STANZA_GIT_PASSWORD"]);
        Assert.Equal("keep authored file", File.ReadAllText(Path.Combine(fetch.WorkingDirectory, "../authored.txt")));
        Assert.All(runner.Requests.Where(invocation => invocation.Executable.EndsWith("git", StringComparison.Ordinal)), invocation =>
        {
            Assert.Contains("http.followRedirects=false", invocation.Arguments);
            Assert.DoesNotContain("source-password", string.Join(' ', invocation.Arguments));
        });
    }

    [Fact]
    public async Task GitReferenceListingPreservesTagsDistinctFromSameNamedBranches()
    {
        using var root = new AgentTestDirectory(); var hash = new string('d', 40);
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = _ => new(0,
            $"{hash}\trefs/heads/release\n{hash}\trefs/tags/release\n{hash}\trefs/tags/v1\n", "", false, false) };
        var request = AgentWorkExecutorTests.Request("git-branches") with { DeploymentId = Guid.Empty,
            Sources = [new("https://git.example/repo", "HEAD", "checkout")] };
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(["refs/tags/release", "refs/tags/v1", "release"], JsonSerializer.Deserialize<string[]>(result.Output!)!);
        var invocation = Assert.Single(runner.Requests);
        Assert.Contains("--heads", invocation.Arguments); Assert.Contains("--tags", invocation.Arguments); Assert.Contains("--refs", invocation.Arguments);
    }

    [Fact]
    public async Task EnvironmentPreviewUsesComposeAuthoritativeEnvironmentOutputAndAllOrderedScopes()
    {
        using var root = new AgentTestDirectory(); var request = AgentWorkExecutorTests.Request("environment-preview");
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = invocation =>
        {
            Assert.Equal("--environment", invocation.Arguments[^1]); Assert.Equal("config", invocation.Arguments[^2]);
            var paths = invocation.Arguments.Select((value, index) => (value, index)).Where(pair => pair.value == "--env-file")
                .Select(pair => invocation.Arguments[pair.index + 1]).ToArray();
            Assert.Equal(request.EnvironmentFiles.Select(file => file.Text), paths.Select(File.ReadAllText));
            return new(0, "VALUE=deployment\nCOMPOSE_VALUE=expanded-by-compose\n", "", false, false);
        }};
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code); Assert.Equal("VALUE=deployment\nCOMPOSE_VALUE=expanded-by-compose\n", result.Output);
        Assert.Single(runner.Requests); Assert.DoesNotContain("expanded-by-compose", result.ToString());
    }

    [Fact]
    public async Task ActualGitRefusesAuthenticatedHttpRedirectWithoutSendingCredentialsToTheRedirectTarget()
    {
        var redirectedRequests = 0; var authenticatedRequests = 0;
        await using var target = await HttpServerAsync(context =>
        {
            Interlocked.Increment(ref redirectedRequests); context.Response.StatusCode = 500; return Task.CompletedTask;
        });
        await using var source = await HttpServerAsync(context =>
        {
            if (context.Request.Headers.Authorization.Count == 0)
            {
                context.Response.StatusCode = 401; context.Response.Headers.WWWAuthenticate = "Basic realm=\"private-git\"";
            }
            else
            {
                Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("source-user:source-password")), context.Request.Headers.Authorization.ToString());
                Interlocked.Increment(ref authenticatedRequests); context.Response.StatusCode = 302;
                context.Response.Headers.Location = target.Urls.Single() + context.Request.Path + context.Request.QueryString;
            }
            return Task.CompletedTask;
        });
        using var root = new AgentTestDirectory();
        var executor = new AgentWorkExecutor(new ProcessRunner(TimeProvider.System), Options.Create(new AgentOptions
            { PersistentDataRoot = root.Path, AllowLoopbackHttp = true }), Options.Create(new DockerRuntimeOptions()), Options.Create(new HostExecutionOptions()));
        var request = AgentWorkExecutorTests.Request("git-branches") with { DeploymentId = Guid.Empty,
            Sources = [new(source.Urls.Single() + "/repository.git", "HEAD", "checkout", "source-user", "source-password")] };
        var result = await executor.ExecuteAsync(request);
        Assert.Equal("git_failed", result.Code); Assert.Null(result.Output);
        Assert.True(Volatile.Read(ref authenticatedRequests) > 0); Assert.Equal(0, Volatile.Read(ref redirectedRequests));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root.Path, "credentials")));
    }

    private static string ContainerInspection(string id, Guid deployment, string status, string health) => JsonSerializer.Serialize(new[]
    {
        new { Id = id, Image = "sha256:" + new string('e', 64), Config = new { Labels = new Dictionary<string, string>
            { ["com.docker.compose.project"] = "stanza-" + deployment.ToString("N") } },
            State = new { Status = status, Health = new { Status = health } } }
    });

    private static async Task<WebApplication> HttpServerAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        var app = builder.Build(); app.Run(handler); await app.StartAsync(); return app;
    }
}
