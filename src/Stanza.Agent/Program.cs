using Microsoft.Extensions.Options;
using Stanza.Agent.Enrollment;
using Stanza.Agent.Runtime;
using Stanza.Agent.Transport;
using Stanza.Agent.Logs;

namespace Stanza.Agent;

public sealed class AgentEntryPoint
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IValidateOptions<AgentOptions>, AgentOptionsValidator>();
        builder.Services.AddOptions<AgentOptions>().BindConfiguration(AgentOptions.SectionName).ValidateOnStart();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AgentState>();
        builder.Services.AddHttpClient<IEnrollmentClient, EnrollmentClient>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
                // Default platform TLS verification is mandatory. No certificate bypass.
            });
        builder.Services.AddHostedService<EnrollmentWorker>();
        builder.Services.AddOptions<DockerRuntimeOptions>().BindConfiguration("Agent:Docker")
            .Validate(DockerRuntimeOptions.IsValid, "Agent Docker configuration is invalid.").ValidateOnStart();
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<IDockerCapabilitiesProbe, DockerCapabilitiesProbe>();
        builder.Services.AddSingleton<IAgentHostPathResolver, DockerAgentHostPathResolver>();
        builder.Services.AddSingleton<IAgentWorkExecutor, AgentWorkExecutor>();
        builder.Services.AddSingleton<AgentJobStore>();
        builder.Services.AddSingleton<IAgentJournalStorage, AgentJournalStorage>();
        builder.Services.AddSingleton<IAgentDiskSpace, AgentDiskSpaceReader>();
        builder.Services.AddSingleton<AgentStorageHealth>();
        builder.Services.AddSingleton<IAgentCommandChannel, AgentCommandChannel>();
        builder.Services.AddSingleton<AgentLogManager>();
        builder.Services.Configure<HostExecutionOptions>(builder.Configuration.GetSection("Agent:HostExecution"));

        var app = builder.Build();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", (AgentState state, AgentStorageHealth health) =>
        {
            var snapshot = state.Snapshot;
            var storage = health.Read();
            var body = new
            {
                status = snapshot.Ready && storage.Healthy ? "ready" : "unavailable",
                enrollment = snapshot.Enrollment.ToString().ToLowerInvariant(),
                snapshot.CommandChannelAvailable,
                CanExecuteWork = snapshot.CanExecuteWork && storage.Healthy,
                storage
            };
            return Results.Json(body, statusCode: snapshot.Ready && storage.Healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });
        await app.RunAsync();
    }
}
