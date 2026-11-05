using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Operations;

/// <summary>Reattaches to accepted operations after a Control Plane restart; never submits work.</summary>
public sealed class OperationRecoveryWorker(IServiceScopeFactory scopes, IAgentCommandDispatcher dispatcher,
    ILogger<OperationRecoveryWorker> logger, InstanceMaintenance? maintenance = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (dispatcher is not IAgentJobDispatcher jobs) return;
        var active = new Dictionary<Guid, Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var agent in active.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray())
                    { await active[agent]; active.Remove(agent); }
                    if (dispatcher is IAgentConnectionCatalog catalog && catalog.ConnectedAgentIds.Count == 0)
                    { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
                    if (maintenance?.Paused != true)
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
                        var rows = await db.Set<OperationEntity>().AsNoTracking().Where(x => x.State == "Unknown")
                            .OrderBy(x => x.CreatedAt).ToListAsync(stoppingToken);
                        var uncertain = await db.Set<AutomationRun>().AsNoTracking().Where(x => x.State == "uncertain").Select(x => x.Id).ToArrayAsync(stoppingToken);
                        var triggers = uncertain.Select(id => "automation:" + id.ToString("N")).ToArray();
                        rows.AddRange(await db.Set<OperationEntity>().AsNoTracking().Where(x => triggers.Contains(x.Trigger) && x.State != "Unknown")
                            .OrderBy(x => x.CreatedAt).ToListAsync(stoppingToken));
                        foreach (var group in rows.GroupBy(x => x.AgentId))
                            if (!active.ContainsKey(group.Key) && dispatcher.IsConnected(group.Key))
                                active[group.Key] = RecoverAgentAsync(group.ToArray());
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                { logger.LogWarning("The execution recovery queue could not be inspected ({ErrorType}); locks are retained.", error.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(active.Values); }
        async Task RecoverAgentAsync(IReadOnlyList<OperationEntity> rows)
        {
            // Missing/ambiguous evidence for one operation must not starve other recoveries
            // on the same Agent. Inspect each candidate without ever resubmitting its command.
            foreach (var row in rows)
            {
                if (stoppingToken.IsCancellationRequested) break;
                await RecoverAsync(row, jobs, stoppingToken);
            }
        }
    }
    private async Task RecoverAsync(OperationEntity row, IAgentJobDispatcher jobs, CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task? monitor = null;
        async Task Output(Guid id, AgentOutputChunk chunk)
        {
            if (id != row.Id) return;
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OperationOutputStore>().AppendAsync(id, chunk, lifetime.Token);
        }
        async Task Progress(Guid id, string phase)
        {
            if (id != row.Id) return;
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().Where(x => x.Id == id && x.State == "Unknown")
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Stage, phase), lifetime.Token);
        }
        var feed = dispatcher as IAgentWorkOutputSource; var progress = dispatcher as IAgentWorkProgressSource;
        try
        {
            if (row.State != "Unknown")
            {
                await using var completedScope = scopes.CreateAsyncScope();
                await completedScope.ServiceProvider.GetRequiredService<OperationRecoveryService>().RecoverKnownAsync(row.Id, stoppingToken);
                return;
            }
            var inspection = await jobs.InspectAsync(row.AgentId, row.Id, lifetime.Token);
            if (!inspection.Found) return;
            if (feed is not null) feed.Output += Output;
            if (progress is not null) progress.Progress += Progress;
            if (inspection.Active) monitor = MonitorAsync();
            var response = await jobs.AttachAsync(row.AgentId, row.Id, lifetime.Token);
            await lifetime.CancelAsync();
            if (monitor is not null) { await monitor; monitor = null; }
            if (feed is not null) feed.Output -= Output;
            if (progress is not null) progress.Progress -= Progress;
            if (response.Code is "uncertain" or "agent_busy" || stoppingToken.IsCancellationRequested) return;
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OperationRecoveryService>().RecoverKnownAsync(row.Id, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is OperationException or AgentUnavailableException or IOException or TimeoutException)
        { logger.LogInformation("Execution recovery for {OperationId} remains unresolved ({ErrorType}); locks are retained.", row.Id, error.GetType().Name); }
        catch (Exception error)
        { logger.LogError("Execution recovery for {OperationId} failed ({ErrorType}); locks are retained.", row.Id, error.GetType().Name); }
        finally
        {
            if (feed is not null) feed.Output -= Output;
            if (progress is not null) progress.Progress -= Progress;
            await lifetime.CancelAsync();
            if (monitor is not null) await monitor;
        }
        async Task MonitorAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
                        var current = await db.Set<OperationEntity>().AsNoTracking().SingleAsync(x => x.Id == row.Id, lifetime.Token);
                        var reason = current.CancellationReason;
                        try { await scope.ServiceProvider.GetRequiredService<OperationService>().AuthorizeContinuationAsync(current, lifetime.Token); }
                        catch (Exception error) when (error is OperationException or AutomationAccessException) { reason ??= "authorization_revoked"; }
                        if (reason is "user" or "deadline" or "authorization_revoked")
                            await jobs.CancelAsync(row.AgentId, row.Id, reason, lifetime.Token);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                    catch (Exception error)
                    { logger.LogWarning("Recovery authorization monitoring for {OperationId} could not complete ({ErrorType}).", row.Id, error.GetType().Name); }
                    await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
}
