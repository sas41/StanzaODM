namespace Stanza.ControlPlane.Automation;

public sealed class AutomationWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AutomationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(ProduceAsync(stoppingToken), ConsumeAsync(stoppingToken));

    private async Task ProduceAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<Configuration.InstanceMaintenance>()?.Paused == true) { await Task.Delay(TimeSpan.FromSeconds(1), clock, ct); continue; }
                await scope.ServiceProvider.GetRequiredService<AutomationScheduler>().TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Automation scheduling paused after a storage/configuration failure; no payloads were logged."); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), clock, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<Configuration.InstanceMaintenance>()?.Paused == true) { await Task.Delay(TimeSpan.FromSeconds(1), clock, ct); continue; }
                if (await scope.ServiceProvider.GetRequiredService<AutomationExecutor>().ExecuteNextAsync(ct)) continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Automation execution paused after a storage failure; interrupted work is not replayed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), clock, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
