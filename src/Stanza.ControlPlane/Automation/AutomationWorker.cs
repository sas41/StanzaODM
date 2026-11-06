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
        var active = new Dictionary<Guid, Task>();
        async Task RunAsync(AsyncServiceScope scope, AutomationExecutor executor, AutomationRun run)
        { await using (scope) await executor.ExecuteClaimedAsync(run, ct); }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var id in active.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray())
                { var completed = active[id]; active.Remove(id); await completed; }
                var scope = scopes.CreateAsyncScope();
                try
                {
                    if (scope.ServiceProvider.GetService<Configuration.InstanceMaintenance>()?.Paused != true)
                    {
                        var executor = scope.ServiceProvider.GetRequiredService<AutomationExecutor>();
                        if (await executor.ClaimNextAsync(ct) is { } run)
                        { active.Add(run.Id, RunAsync(scope, executor, run)); continue; }
                    }
                }
                catch { await scope.DisposeAsync(); throw; }
                await scope.DisposeAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Automation execution paused after a storage failure; interrupted work is not replayed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), clock, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
        try { await Task.WhenAll(active.Values); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
