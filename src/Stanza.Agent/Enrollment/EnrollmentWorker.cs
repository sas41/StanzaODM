using Microsoft.Extensions.Options;
using Stanza.Agent.Transport;

namespace Stanza.Agent.Enrollment;

public sealed class EnrollmentWorker(
    IOptions<AgentOptions> options,
    IEnrollmentClient client,
    AgentState state,
    TimeProvider timeProvider,
    ILogger<EnrollmentWorker> logger,
    IAgentCommandChannel? channel = null) : BackgroundService
{
    public static TimeSpan RetryDelay(int consecutiveFailures) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Clamp(consecutiveFailures, 1, 6))));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentIdentity identity;
        try
        {
            identity = AgentIdentity.LoadOrCreate(options.Value.PersistentDataRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            state.Set(new(false, EnrollmentStatus.Unavailable, null));
            logger.LogError("Agent identity storage is unavailable; check the persistent directory and private file permissions.");
            return;
        }

        using (identity)
        {
            var failures = 0;
            state.Set(new(true, EnrollmentStatus.NotStarted, identity.AgentId));
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var request = identity.CreateEnrollmentRequest(options.Value.EnrollmentToken,
                        Environment.MachineName, timeProvider.GetUtcNow());
                    var status = await client.RegisterAsync(new Uri(options.Value.ControlPlaneUrl), request, stoppingToken);
                    state.Set(new(status is EnrollmentStatus.NotStarted or EnrollmentStatus.Pending or EnrollmentStatus.Retrying, status, identity.AgentId));
                    if (status == EnrollmentStatus.Approved && channel is not null)
                    {
                        try { await channel.RunAsync(identity, stoppingToken); }
                        catch (AgentChannelProtocolException)
                        {
                            state.Set(new(false, EnrollmentStatus.Incompatible, identity.AgentId));
                            logger.LogWarning("Agent command channel protocol is incompatible; execution remains unavailable.");
                            await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, stoppingToken);
                            return;
                        }
                        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or IOException or
                            System.Text.Json.JsonException or OperationCanceledException or System.Net.Http.HttpRequestException)
                        {
                            if (stoppingToken.IsCancellationRequested) break;
                            logger.LogWarning("Agent command channel disconnected; active work is not automatically repeated.");
                        }
                        state.Set(new(false, EnrollmentStatus.Retrying, identity.AgentId));
                        failures = Math.Min(failures + 1, 6);
                        await Task.Delay(RetryDelay(failures), timeProvider, stoppingToken);
                        continue;
                    }
                    if (status is EnrollmentStatus.Rejected or EnrollmentStatus.Revoked or EnrollmentStatus.Incompatible)
                    {
                        logger.LogWarning("Agent enrollment halted with status {EnrollmentStatus}; execution remains unavailable.", status);
                        await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, stoppingToken);
                        return;
                    }

                    failures = status == EnrollmentStatus.Retrying ? Math.Min(failures + 1, 6) : 0;
                    await Task.Delay(failures == 0 ? TimeSpan.FromSeconds(30) : RetryDelay(failures), timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            finally { state.Set(state.Snapshot with { Ready = false, CommandChannelAvailable = false, CanExecuteWork = false }); }
        }
    }
}
