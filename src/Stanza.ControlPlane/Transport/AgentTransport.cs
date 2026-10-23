using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Transport;

public static class AgentTransportExtensions
{
    public static IServiceCollection AddStanzaAgentTransport(this IServiceCollection services)
    {
        services.AddSingleton<AgentCommandBroker>();
        services.AddSingleton<IAgentCommandDispatcher>(sp => sp.GetRequiredService<AgentCommandBroker>());
        services.TryAddScoped<InstanceSettingsService>();
        services.TryAddScoped<AccessService>();
        services.TryAddScoped<OperationalPolicyService>();
        services.TryAddScoped<AgentPolicyService>();
        services.AddScoped<AgentConfigurationSync>();
        return services;
    }

    public static IEndpointConventionBuilder MapStanzaAgentTransport(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/agent-channel", HandleAsync);

    private static async Task HandleAsync(HttpContext context, AgentCommandBroker broker,
        AgentAdministrationService administration, StanzaDbContext store, IHostEnvironment environment, TimeProvider clock)
    {
        if (!context.Request.IsHttps && !(environment.IsDevelopment() && context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address)))
        { context.Response.StatusCode = 400; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var header = context.Request.Headers["X-Stanza-Proof"];
        if (header.Count != 1 || header[0] is not { Length: > 0 and <= 16384 } encoded)
        { context.Response.StatusCode = 401; return; }
        AgentEnrollmentRequest? proof;
        try { proof = JsonSerializer.Deserialize<AgentEnrollmentRequest>(Convert.FromBase64String(encoded), JsonSerializerOptions.Web); }
        catch (Exception ex) when (ex is FormatException or JsonException) { context.Response.StatusCode = 401; return; }
        try
        {
            if (proof is not null && proof.ProtocolVersion != AgentEnrollmentProtocol.Version)
            { context.Response.StatusCode = 426; return; }
            if (proof is null || !AgentEnrollmentProtocol.VerifyProof(proof, clock.GetUtcNow()))
            { context.Response.StatusCode = 401; return; }
            var result = await administration.RegisterAsync(proof, context.RequestAborted);
            if (result.Status != AgentEnrollmentProtocol.Approved) { context.Response.StatusCode = 403; return; }
            var agent = await store.Set<AgentEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Identity == proof.AgentId &&
                x.PublicKey == proof.PublicKey && x.Status == AgentEnrollmentProtocol.Approved, context.RequestAborted);
            if (agent is null) { context.Response.StatusCode = 403; return; }
            // Reserve before upgrading: one live channel per persistent identity, never silently replace it.
            var session = broker.Reserve(agent.Id);
            if (session is null) { context.Response.StatusCode = 409; return; }
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await session.RunAsync(socket, async token =>
                {
                    var valid = await store.Set<AgentEntity>().AsNoTracking().AnyAsync(x => x.Id == agent.Id &&
                        x.Identity == proof.AgentId && x.PublicKey == proof.PublicKey && x.Status == AgentEnrollmentProtocol.Approved, token);
                    // Revoking the bound enrollment credential must terminate its existing session as well.
                    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(proof.Token)));
                    valid &= await store.Set<EnrollmentTokenEntity>().AsNoTracking().AnyAsync(x => x.Hash == hash &&
                        x.BoundIdentity == proof.AgentId && !x.Revoked, token);
                    if (valid)
                        await store.Set<AgentEntity>().Where(x => x.Id == agent.Id).ExecuteUpdateAsync(
                            update => update.SetProperty(x => x.LastSeenAt, clock.GetUtcNow().ToUnixTimeSeconds()), token);
                    return valid;
                }, context.RequestAborted, async (send, expecting, ct) =>
                {
                    var transfer = new AgentConfigurationTransfer();
                    var scopes = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
                    while (!ct.IsCancellationRequested)
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        if (scope.ServiceProvider.GetService<InstanceMaintenance>()?.Paused == true)
                        { await Task.Delay(TimeSpan.FromSeconds(1), ct); continue; }
                        var snapshot = await scope.ServiceProvider.GetRequiredService<AgentConfigurationSync>().ReadAsync(agent.Id, ct);
                        await transfer.SendAsync(snapshot, send, expecting, ct);
                        using (var acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            acknowledgement.CancelAfter(TimeSpan.FromMinutes(2));
                            while (session.ConfigurationPending) await Task.Delay(TimeSpan.FromMilliseconds(100), acknowledgement.Token);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(snapshot.CheckIntervalSeconds), ct);
                    }
                });
            }
            finally { broker.Release(agent.Id, session); }
        }
        catch (AgentOperationException ex) when (!context.Response.HasStarted) { context.Response.StatusCode = ex.StatusCode; }
    }
}

public sealed class AgentCommandBroker : IAgentCommandDispatcher, IAgentWorkProgressSource, IAgentConnectionCatalog
{
    public event Func<Guid, string, Task>? Progress;
    private async Task ReportAsync(Guid id, string stage)
    {
        if (Progress is not { } progress) return;
        foreach (Func<Guid, string, Task> handler in progress.GetInvocationList()) await handler(id, stage);
    }
    private readonly ConcurrentDictionary<Guid, AgentCommandSession> sessions = new();
    public IReadOnlyList<Guid> ConnectedAgentIds => sessions.Where(pair => pair.Value.Connected).Select(pair => pair.Key).ToArray();
    public bool IsConnected(Guid agentId) => sessions.TryGetValue(agentId, out var session) && session.Connected;
    public AgentCapabilities? Capabilities(Guid agentId) => sessions.TryGetValue(agentId, out var session) && session.Connected ? session.Capabilities : null;
    public DateTimeOffset? ConfigurationSyncedAt(Guid agentId) => sessions.TryGetValue(agentId, out var session) && session.Connected ? session.ConfigurationSyncedAt : null;
    public bool ConfigurationPending(Guid agentId) => sessions.TryGetValue(agentId, out var session) && session.Connected && session.ConfigurationPending;
    public Task<Stanza.ControlPlane.Terminals.IAgentTerminalSession> OpenTerminalAsync(Guid agentId, Guid deploymentId,
        AgentExecution execution, AgentRuntimePolicy policy, CancellationToken token = default) =>
        sessions.TryGetValue(agentId, out var session) && session.Connected
            ? session.OpenTerminalAsync(deploymentId, execution, policy, token) : throw new AgentUnavailableException();
    public AgentCommandSession? Reserve(Guid agentId)
    {
        var session = new AgentCommandSession(ReportAsync);
        return sessions.TryAdd(agentId, session) ? session : null;
    }
    public void Release(Guid agentId, AgentCommandSession session)
    {
        sessions.TryRemove(new KeyValuePair<Guid, AgentCommandSession>(agentId, session));
        session.Dispose();
    }
    public Task<AgentWorkResponse> DispatchAsync(Guid agentId, AgentWorkRequest request, CancellationToken token = default) =>
        sessions.TryGetValue(agentId, out var session) && session.Connected
            ? session.DispatchAsync(request, token) : throw new AgentUnavailableException();
}

public sealed class AgentCommandSession : IDisposable
{
    private readonly Func<Guid, string, Task>? report;
    private readonly ConcurrentDictionary<Guid, int> progressCount = new();
    public AgentCommandSession(Func<Guid, string, Task>? report = null) => this.report = report;
    // Wire JSON is never embedded in HTML; base64 must not inflate beyond its declared frame bound.
    private static readonly JsonSerializerOptions TerminalJson = new(JsonSerializerDefaults.Web)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly SemaphoreSlim send = new(1, 1);
    private readonly SemaphoreSlim execution = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentWorkResponse>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, BrokerTerminalSession> terminals = new();
    private readonly SemaphoreSlim terminalCapacity = new(16, 16);
    public AgentCapabilities? Capabilities { get; private set; }
    private string? expectedConfiguration;
    private string? appliedConfiguration;
    private long configurationSyncedAt;
    public DateTimeOffset? ConfigurationSyncedAt
    {
        get
        {
            var value = Interlocked.Read(ref configurationSyncedAt);
            return value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;
        }
    }
    public bool ConfigurationPending => expectedConfiguration != appliedConfiguration;
    private WebSocket? socket;
    private int ready;
    public bool Connected => Volatile.Read(ref ready) == 1 && socket?.State == WebSocketState.Open && !lifetime.IsCancellationRequested;

    public async Task<Stanza.ControlPlane.Terminals.IAgentTerminalSession> OpenTerminalAsync(Guid deploymentId,
        AgentExecution target, AgentRuntimePolicy policy, CancellationToken token)
    {
        if (!Connected || Capabilities is not { TerminalAvailable: true }) throw new AgentUnavailableException();
        if (deploymentId == Guid.Empty || !policy.ExecutionEnabled || target.Mode is not ("exec" or "logs")) throw new IOException("Invalid stream target, mode, or policy.");
        if (!await terminalCapacity.WaitAsync(0, token)) throw new IOException("Agent terminal limit reached.");
        var id = Guid.NewGuid();
        var session = new BrokerTerminalSession(id, SendAsync, key => { if (terminals.TryRemove(key, out _)) terminalCapacity.Release(); }, target.Mode == "logs");
        if (!terminals.TryAdd(id, session)) { terminalCapacity.Release(); throw new IOException("Duplicate terminal identity."); }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            await SendAsync(new("terminal.open", id, new(id, deploymentId, "terminal.open", [], [], target, Policy: policy)), timeout.Token);
            await session.Opened.WaitAsync(timeout.Token);
            return session;
        }
        catch { await session.CloseAsync(CancellationToken.None); throw; }
    }

    public async Task<AgentWorkResponse> DispatchAsync(AgentWorkRequest request, CancellationToken token)
    {
        if (request.Id == Guid.Empty) return new(request.Id, false, "invalid_request");
        if (!await execution.WaitAsync(0, token)) return new(request.Id, false, "agent_busy");
        var completion = new TaskCompletionSource<AgentWorkResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = false;
        try
        {
            if (!Connected) throw new AgentUnavailableException();
            if (!pending.TryAdd(request.Id, completion)) return new(request.Id, false, "duplicate_pending_request");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(request.Policy?.TimeoutSeconds ?? 300, 1, 3600) + 30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, timeout.Token);
            attempted = true;
            await SendAsync(new("work.request", request.Id, request), linked.Token);
            var response = await completion.Task.WaitAsync(linked.Token);
            if (response.Output?.Length > Math.Clamp(request.Policy?.MaximumOutputCharacters ?? 65536, 1, 1048576))
            { Dispose(); return new(request.Id, false, "uncertain"); }
            return response;
        }
        catch (Exception ex) when (attempted && ex is OperationCanceledException or WebSocketException or IOException)
        {
            // Once a send is attempted, a missing acknowledgement is not evidence that execution did not occur.
            // Cancel the entire channel so the Agent cancels its active process; do not permit queued work to race it.
            Dispose();
            return new(request.Id, false, "uncertain");
        }
        finally { pending.TryRemove(request.Id, out _); progressCount.TryRemove(request.Id, out _); execution.Release(); }
    }

    public async Task RunAsync(WebSocket connectedSocket, Func<CancellationToken, Task<bool>> validate, CancellationToken token,
        Func<Func<AgentWireMessage, CancellationToken, Task>, Action<string>, CancellationToken, Task>? synchronize = null)
    {
        socket = connectedSocket;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var monitor = MonitorAsync(validate, linked.Token);
        Task? synchronization = null;
        try
        {
            await SendAsync(new("hello", Guid.Empty, Data: AgentWireMessage.Version.ToString()), linked.Token);
            Volatile.Write(ref ready, 1);
            while (!linked.IsCancellationRequested)
            {
                var message = await AgentSocketFrames.ReceiveAsync(socket, linked.Token);
                if (message is null) break;
                if (message.Type == "configuration.request")
                {
                    if (synchronize is null || synchronization is not null || message.Id != Guid.Empty || message.Data is not null)
                        throw new IOException("Invalid configuration request.");
                    synchronization = SynchronizeAsync();
                }
                else if (message.Type == "configuration.applied")
                {
                    if (message.Id != Guid.Empty || expectedConfiguration is null || message.Data != expectedConfiguration)
                        throw new IOException("Invalid configuration acknowledgement.");
                    appliedConfiguration = message.Data;
                    Interlocked.Exchange(ref configurationSyncedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                else if (message.Type == "work.response" && message.Response is { } response && response.Id == message.Id)
                {
                    if (response.Output?.Length > 1048576 || response.Code is not { Length: > 0 and <= 100 } ||
                        !response.Code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ||
                        response.ResolvedSources?.Count > 32 || response.ResolvedImages?.Count > 10000)
                        throw new IOException("Invalid Agent response.");
                    if (pending.TryGetValue(message.Id, out var completion)) completion.TrySetResult(response);
                }
                else if (message.Type == "work.progress")
                {
                    if (!pending.ContainsKey(message.Id)) continue;
                    if (message.Data is not ("Validating" or "Preparing" or "Building" or "Pulling" or "Applying" or "Checking") || progressCount.AddOrUpdate(message.Id, 1, (_, count) => count + 1) > 32) throw new IOException("Invalid operation progress.");
                    if (report is not null) await report(message.Id, message.Data);
                }
                else if (message.Type == "capabilities")
                {
                    if (message.Id != Guid.Empty || message.Data is not { Length: > 0 and <= 2048 }) throw new IOException("Invalid capabilities.");
                    var caps = JsonSerializer.Deserialize<AgentCapabilities>(message.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 });
                    static bool Safe(string? value, int max) => value is null || value.Length is > 0 && value.Length <= max && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_');
                    if (caps is null || caps.Runtime != "docker" || caps.Architecture is null || !Safe(caps.Architecture, 32) ||
                        !Safe(caps.DockerVersion, 64) || !Safe(caps.ComposeVersion, 64) || caps.TerminalAvailable && !caps.DockerAvailable)
                        throw new IOException("Invalid capabilities.");
                    Capabilities = caps;
                }
                else if (message.Type is "terminal.opened" or "terminal.output" or "terminal.exited")
                {
                    if (message.Id == Guid.Empty || message.Data?.Length > 87384) throw new IOException("Invalid terminal frame.");
                    if (terminals.TryGetValue(message.Id, out var terminal) && !terminal.Receive(message))
                    {
                        // A slow browser closes its terminal only; normal deployment traffic remains duplex.
                        _ = terminal.CloseAsync(linked.Token).AsTask();
                    }
                }
                else if (message.Type == "ping") await SendAsync(new("pong", message.Id), linked.Token);
                else if (message.Type != "pong") throw new IOException("Unsupported Agent message.");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or JsonException or FormatException) { }
        finally
        {
            linked.Cancel();
            Dispose();
            try { await monitor; } catch (OperationCanceledException) { }
            if (synchronization is not null) await synchronization;
        }
        async Task SynchronizeAsync()
        {
            try { await synchronize!(SendAsync, hash => expectedConfiguration = hash, linked.Token); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception)
            {
                // Close on failed replication. Reconnect retries a full snapshot, never a partial delta.
                Dispose();
            }
        }
    }

    private async Task MonitorAsync(Func<CancellationToken, Task<bool>> validate, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                if (!await validate(token)) { Dispose(); return; }
                await SendAsync(new("ping", Guid.NewGuid()), token);
            }
        }
        catch (Exception) when (!token.IsCancellationRequested) { Dispose(); }
    }

    private async Task SendAsync(AgentWireMessage message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, message.Type.StartsWith("terminal.", StringComparison.Ordinal) ? TerminalJson : JsonSerializerOptions.Web);
        if (bytes.Length > AgentSocketFrames.MaximumBytes) throw new IOException("Agent frame exceeds the limit.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await send.WaitAsync(timeout.Token);
        try { await socket!.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token); }
        finally { send.Release(); }
    }
    public void Dispose()
    {
        Volatile.Write(ref ready, 0);
        lifetime.Cancel();
        socket?.Abort();
        Capabilities = null;
        foreach (var terminal in terminals.Values) terminal.Complete();
        foreach (var pair in pending) pair.Value.TrySetResult(new(pair.Key, false, "uncertain"));
    }
}

public static class AgentSocketFrames
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    public static async Task<AgentWireMessage?> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16384];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text || stream.Length + part.Count > MaximumBytes)
                throw new IOException("Invalid Agent frame.");
            stream.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) break;
        }
        var message = JsonSerializer.Deserialize<AgentWireMessage>(stream.GetBuffer().AsSpan(0, (int)stream.Length), JsonSerializerOptions.Web)
            ?? throw new IOException("Invalid Agent message.");
        if (message.Type is not { Length: > 0 and <= 64 }) throw new IOException("Invalid Agent message type.");
        if (message.Type.StartsWith("terminal.", StringComparison.Ordinal) &&
            (stream.Length > 96 * 1024 || message.Request is not null || message.Response is not null || message.Type == "terminal.opened" && message.Data is not null))
            throw new IOException("Invalid terminal frame.");
        return message;
    }
}
