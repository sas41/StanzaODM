using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Enrollment;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class AgentHealthTests
{
    [Fact]
    public void MissingEnrollmentSecretFailsStartupWithoutDisclosingConfiguration()
    {
        using var directory = new AgentTestDirectory();
        using var factory = new AgentFactory(directory.Path, "");
        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("EnrollmentToken", exception.Message);
        Assert.DoesNotContain("control.example", exception.Message);
    }

    [Fact]
    public async Task HostedAgentHasHealthEndpointsButNoCommandEndpoint()
    {
        using var directory = new AgentTestDirectory();
        using var factory = new AgentFactory(directory.Path);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        await factory.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AgentTestWait.UntilAsync(() => factory.Services.GetRequiredService<AgentState>().Snapshot.Enrollment == EnrollmentStatus.Pending);
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var json = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("pending", json.RootElement.GetProperty("enrollment").GetString());
        Assert.False(json.RootElement.GetProperty("commandChannelAvailable").GetBoolean());
        Assert.False(json.RootElement.GetProperty("canExecuteWork").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/commands", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/agent-enrollment", null)).StatusCode);
        factory.Services.GetRequiredService<AgentState>().Set(new(false, EnrollmentStatus.Unavailable, null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task ReadinessIncludesDiskHeadroomAndUnresolvedJournalFailuresWithoutLeakingErrorMessages()
    {
        using var directory = new AgentTestDirectory(); var disk = new Disk();
        using var factory = new AgentFactory(directory.Path, disk: disk); using var client = factory.CreateClient();
        await factory.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AgentTestWait.UntilAsync(() => factory.Services.GetRequiredService<AgentState>().Snapshot.Enrollment == EnrollmentStatus.Pending);
        factory.Services.GetRequiredService<AgentState>().Set(new(true, EnrollmentStatus.Approved, "test") { CommandChannelAvailable = true, CanExecuteWork = true });
        disk.Available = 512 * 1024 * 1024;
        using (var response = await client.GetAsync("/health/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("canExecuteWork").GetBoolean());
            Assert.Equal(disk.Available, json.RootElement.GetProperty("storage").GetProperty("availableBytes").GetInt64());
        }
        disk.Available = 10L * 1024 * 1024 * 1024;
        var health = factory.Services.GetRequiredService<AgentStorageHealth>(); var id = Guid.NewGuid();
        health.Failed(id, "journal", new IOException("sensitive-host-path"));
        using (var response = await client.GetAsync("/health/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("sensitive-host-path", await response.Content.ReadAsStringAsync());
        }
        health.Durable(id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    private sealed class Disk : IAgentDiskSpace
    {
        public long Available = 10L * 1024 * 1024 * 1024;
        public AgentDiskSpace Read(string path) => new(Available, 100L * 1024 * 1024 * 1024);
    }

    private sealed class AgentFactory(string root, string? enrollmentToken = null, IAgentDiskSpace? disk = null) : WebApplicationFactory<AgentEntryPoint>
    {
        public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:PersistentDataRoot"] = root,
                ["Agent:ControlPlaneUrl"] = "https://control.example",
                ["Agent:EnrollmentToken"] = enrollmentToken ?? new string('t', 32)
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEnrollmentClient>();
                services.AddSingleton<IEnrollmentClient>(new PendingClient(Registered));
                if (disk is not null) { services.RemoveAll<IAgentDiskSpace>(); services.AddSingleton(disk); }
            });
        }
    }

    private sealed class PendingClient(TaskCompletionSource registered) : IEnrollmentClient
    {
        public Task<EnrollmentStatus> RegisterAsync(Uri controlPlane, AgentEnrollmentRequest request, CancellationToken cancellationToken)
        {
            registered.TrySetResult();
            return Task.FromResult(EnrollmentStatus.Pending);
        }
    }
}
