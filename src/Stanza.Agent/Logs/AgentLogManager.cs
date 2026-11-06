using System.Collections.Concurrent;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Logs;

/// <summary>Ephemeral read-only Docker log subscriptions. No exec, PTY, input or workspace.</summary>
public sealed class AgentLogManager
{
    public const int MaximumSessions = 16;
    private readonly ConcurrentDictionary<Guid, Session> sessions = new();
    private readonly SemaphoreSlim capacity = new(MaximumSessions, MaximumSessions);

    public async Task HandleAsync(AgentWireMessage message, Func<AgentWireMessage, Task> send, CancellationToken token)
    {
        if (message.Id == Guid.Empty) throw new IOException("Invalid log session.");
        if (message.Type == "log.close")
        {
            if (sessions.TryGetValue(message.Id, out var current)) current.Cancel();
            return;
        }
        if (message.Type != "log.open" || message.Request is not { } request || request.Id != message.Id || request.Operation != "log.open" ||
            request.Policy is not { ExecutionEnabled: true, TimeoutSeconds: >= 0 and <= 604800 } || !AgentLogProtocol.IsValid(request.Execution) ||
            request.Files is not { Count: 0 } || request.EnvironmentFiles is not { Count: 0 } || request.Sources?.Count > 0 || request.Registries?.Count > 0)
            throw new IOException("Invalid read-only log subscription.");
        if (!await capacity.WaitAsync(0, token)) { await send(new("log.ended", message.Id, Data: "125")); return; }
        var session = new Session(request, send, token);
        if (!sessions.TryAdd(message.Id, session)) { capacity.Release(); session.Dispose(); throw new IOException("Duplicate log session."); }
        session.Work = RunAsync();
        async Task RunAsync()
        {
            try { await session.RunAsync(); }
            finally { sessions.TryRemove(message.Id, out _); session.Dispose(); capacity.Release(); }
        }
    }

    public async Task CloseAllAsync()
    {
        var active = sessions.Values.ToArray();
        foreach (var session in active) session.Cancel();
        await Task.WhenAll(active.Select(x => x.Work ?? Task.CompletedTask));
    }

    private sealed class Session : IDisposable
    {
        private readonly AgentWorkRequest request;
        private readonly Func<AgentWireMessage, Task> send;
        private readonly CancellationTokenSource lifetime;
        public Task? Work { get; set; }
        public Session(AgentWorkRequest request, Func<AgentWireMessage, Task> send, CancellationToken token)
        {
            this.request = request; this.send = send;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            lifetime.CancelAfter(LimitPolicy.Duration(request.Policy!.TimeoutSeconds));
        }
        public void Cancel() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
        public async Task RunAsync()
        {
            var exit = 125;
            try
            {
                using var client = new DockerLogClient(request.Policy!.DockerEndpoint);
                using var opening = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                opening.CancelAfter(TimeSpan.FromSeconds(20));
                var target = await client.ResolveTargetAsync(request.DeploymentId, request.Execution!, opening.Token);
                await using var logs = await client.OpenLogsAsync(target, opening.Token);
                await send(new("log.opened", request.Id));
                var buffer = new byte[AgentLogProtocol.MaximumChunk]; int count;
                while ((count = await logs.ReadAsync(buffer, lifetime.Token)) != 0)
                    await send(new("log.output", request.Id, Data: Convert.ToBase64String(buffer, 0, count)));
                exit = 0;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or HttpRequestException or System.Text.Json.JsonException or
                InvalidOperationException or KeyNotFoundException or ArgumentException or System.Net.WebSockets.WebSocketException) { }
            finally
            {
                try { await send(new("log.ended", request.Id, Data: exit.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
                catch (Exception) { /* The Agent channel may have closed. */ }
            }
        }
        public void Dispose() => lifetime.Dispose();
    }
}
