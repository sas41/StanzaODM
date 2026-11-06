using System.Net;
using Microsoft.AspNetCore.Http;
using Stanza.Agent.Logs;
using Stanza.ControlPlane.Live;
using Stanza.Core.Contracts;
using Stanza.Tests.Web;

namespace Stanza.Tests.Logs;

public sealed class RemovedTerminalTests
{
    [Fact]
    public void AgentPackagingStillIncludesDockerComposeBuildxBashAndOpenSsh()
    {
        var dockerfile = File.ReadAllText(Path.Combine(ArchitectureTests.RepositoryRoot(), "src/Stanza.Agent/Dockerfile"));
        foreach (var dependency in new[] { "openssh-client", "bash", "/usr/bin/docker", "docker-compose", "docker-buildx" }) Assert.Contains(dependency, dockerfile);
    }

    [Fact]
    public async Task InteractiveEndpointsAndAssetsAreNoLongerShipped()
    {
        await using var factory = new StanzaFactory(); await factory.BootstrapAsync();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await Stanza.Tests.Automation.AutomationHttpTests.Login(client, "owner");
        foreach (var route in new[] { "/terminals/" + Guid.NewGuid(), "/live/terminal/" + Guid.NewGuid(), "/js/terminal.js", "/vendor/xterm/xterm.js", "/vendor/xterm/xterm.css" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        Assert.Null(typeof(Stanza.ControlPlane.Logs.IAgentLogSession).GetMethod("WriteInputAsync"));
        Assert.Null(typeof(Stanza.ControlPlane.Logs.IAgentLogSession).GetMethod("ResizeAsync"));
    }

    [Theory]
    [InlineData("terminal.open")]
    [InlineData("terminal.input")]
    [InlineData("terminal.resize")]
    [InlineData("log.input")]
    [InlineData("log.resize")]
    public async Task AgentHasNoInteractiveMessageHandlers(string type)
    {
        var manager = new AgentLogManager(); var sends = 0;
        await Assert.ThrowsAsync<IOException>(() => manager.HandleAsync(new(type, Guid.NewGuid()), _ => { sends++; return Task.CompletedTask; }, default));
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task LogOpenRejectsExecutableArgumentsScriptsAndEnvironmentBeforeAccessingDocker()
    {
        var manager = new AgentLogManager(); var valid = new AgentExecution("logs", [], Service: "app", Mode: "logs");
        foreach (var invalid in new[] { valid with { Executable = "/bin/bash" }, valid with { Arguments = ["-c", "bad"] },
            valid with { Script = "bad" }, valid with { User = "root" }, valid with { WorkingDirectory = "/" },
            valid with { Environment = new Dictionary<string, string> { ["X"] = "bad" } }, valid with { Container = "another" } })
        {
            var id = Guid.NewGuid();
            await Assert.ThrowsAsync<IOException>(() => manager.HandleAsync(new("log.open", id,
                new(id, Guid.NewGuid(), "log.open", [], [], invalid, Policy: new())), _ => throw new InvalidOperationException("Must not open a stream."), default));
        }
    }

    [Fact]
    public void SharedLiveOriginValidationRejectsCrossOriginAndMultipleOrigins()
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "https"; context.Request.Host = new("stanza.test");
        context.Request.Headers.Origin = "https://stanza.test";
        Assert.True(LiveWebSockets.IsSameOrigin(context.Request, null));
        Assert.False(LiveWebSockets.IsSameOrigin(context.Request, "https://other.test"));
        context.Request.Headers.Origin = new Microsoft.Extensions.Primitives.StringValues(["https://stanza.test", "https://evil.test"]);
        Assert.False(LiveWebSockets.IsSameOrigin(context.Request, null));
    }
}
