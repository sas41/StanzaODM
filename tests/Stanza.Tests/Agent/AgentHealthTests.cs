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

    private sealed class AgentFactory(string root, string? enrollmentToken = null) : WebApplicationFactory<AgentEntryPoint>
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
