using System.Net.WebSockets;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Stanza.Agent.Enrollment;
using Stanza.Agent.Runtime;
using Stanza.Agent.Terminals;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Transport;

public interface IAgentCommandChannel
{
    Task RunAsync(AgentIdentity identity, CancellationToken token);
}

public sealed class AgentChannelProtocolException() : IOException("Incompatible Agent channel protocol.");

public sealed class AgentCommandChannel(IOptions<AgentOptions> options, AgentState state,
    IAgentWorkExecutor executor, TimeProvider clock, AgentTerminalManager? terminals = null,
    IContainerRuntime? runtime = null, IOptions<DockerRuntimeOptions>? dockerOptions = null,
    IOptions<HostExecutionOptions>? hostOptions = null, IHostEnvironment? environment = null,
    AgentJobStore? jobs = null) : IAgentCommandChannel
{
    private readonly AgentJobStore jobStore = jobs ?? new(options, executor, clock);
    // Wire JSON is never embedded in HTML. Avoid expanding '+' in bounded base64 terminal payloads.
    private static readonly JsonSerializerOptions TerminalJson = new(JsonSerializerDefaults.Web)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public async Task RunAsync(AgentIdentity identity, CancellationToken token)
    {
        var origin = new Uri(options.Value.ControlPlaneUrl);
        if (!options.Value.AllowsControlPlaneOrigin(origin, environment?.IsDevelopment() == true))
            throw new IOException("Agent command transport requires HTTPS or the explicit insecure HTTP opt-in.");
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        // ClientWebSocket uses the platform certificate chain and hostname verification; no bypass.
        var proof = identity.CreateEnrollmentRequest(options.Value.EnrollmentToken, options.Value.RegistrationName, clock.GetUtcNow());
        socket.Options.SetRequestHeader("X-Stanza-Proof", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(proof, JsonSerializerOptions.Web)));
        var uri = new UriBuilder(origin) { Scheme = origin.Scheme == "https" ? "wss" : "ws", Path = "/api/agent-channel" }.Uri;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var send = new SemaphoreSlim(1, 1);
        Task? work = null;
        var attachedOperations = new HashSet<Guid>();
        var outputAcknowledgements = new ConcurrentDictionary<(Guid Id, long Sequence), TaskCompletionSource>();
        var terminalManager = terminals ?? new AgentTerminalManager();
        // Per-connection state: an interrupted replication always starts again with a full snapshot.
        var configuration = new AgentConfigurationCache(options);
        var disconnectReason = "remote_close";
        async Task SendAsync(AgentWireMessage message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, message.Type.StartsWith("terminal.", StringComparison.Ordinal) ? TerminalJson : JsonSerializerOptions.Web);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await send.WaitAsync(timeout.Token);
            try
            {
                for (var offset = 0; offset < bytes.Length; offset += 16384)
                {
                    var count = Math.Min(16384, bytes.Length - offset);
                    await socket.SendAsync(bytes.AsMemory(offset, count), WebSocketMessageType.Text, offset + count == bytes.Length, timeout.Token);
                }
            }
            finally { send.Release(); }
        }
        async Task ReplayAsync(Guid id, long after)
        {
            string? phase = null;
            while (!lifetime.IsCancellationRequested)
            {
                var snapshot = await jobStore.InspectAsync(id);
                if (!snapshot.Found) { await SendAsync(new("work.response", id, Response: new(id, false, "job_not_found"))); return; }
                if (after > snapshot.LastSequence) throw new IOException("Requested replay cursor exceeds durable Agent output.");
                if (phase != snapshot.Phase && snapshot.Phase != "Accepted")
                { phase = snapshot.Phase; await SendAsync(new("work.progress", id, Data: phase)); }
                var chunks = await jobStore.ReadOutputAsync(id, after);
                foreach (var chunk in chunks)
                {
                    var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var key = (id, chunk.Sequence);
                    if (!outputAcknowledgements.TryAdd(key, acknowledgement)) throw new IOException("Duplicate output sequence.");
                    try
                    {
                        await SendAsync(new("work.output", id, Output: chunk));
                        await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
                        await jobStore.AcknowledgeAsync(id, chunk.Sequence);
                        after = chunk.Sequence;
                    }
                    finally { outputAcknowledgements.TryRemove(key, out _); }
                }
                if (!snapshot.Active && after >= snapshot.LastSequence)
                { await SendAsync(new("work.response", id, Response: snapshot.Response ?? new(id, false, "uncertain"))); return; }
                if (chunks.Count == 0) await Task.Delay(TimeSpan.FromMilliseconds(50), lifetime.Token);
            }
        }
        async Task PumpAsync(Guid id, long after)
        {
            try
            { await ReplayAsync(id, after); }
            catch (Exception error) { disconnectReason = "output_transport_" + Describe(error); lifetime.Cancel(); socket.Abort(); }
        }
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(30));
                await socket.ConnectAsync(uri, connect.Token);
            }
            var hello = await ReceiveAsync(socket, lifetime.Token);
            if (hello?.Type != "hello" || hello.Data != AgentWireMessage.Version.ToString())
                throw new AgentChannelProtocolException();
            state.Set(state.Snapshot with { Ready = false, CommandChannelAvailable = true, CanExecuteWork = false });
            if (runtime is not null)
            {
                var detected = await runtime.DetectAsync(lifetime.Token);
                var endpoint = dockerOptions?.Value.Endpoint;
                var terminalAvailable = detected.DockerAvailable && endpoint is not null &&
                    DockerRuntimeOptions.IsValid(dockerOptions!.Value) && OperatingSystem.IsLinux();
                if (terminalAvailable)
                {
                    try
                    {
                        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        probeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        using var api = new DockerTerminalClient(endpoint!);
                        using var version = await api.JsonAsync(HttpMethod.Get, "version", null, probeTimeout.Token);
                        terminalAvailable = version.RootElement.TryGetProperty("ApiVersion", out var apiVersion) &&
                            apiVersion.GetString() is { Length: > 0 and <= 16 };
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
                    { terminalAvailable = false; }
                }
                await SendAsync(new("capabilities", Guid.Empty, Data: JsonSerializer.Serialize(new AgentCapabilities(
                    detected.Runtime, detected.DockerAvailable, detected.ComposeAvailable, detected.DockerVersion,
                    detected.ComposeVersion, detected.Architecture, hostOptions?.Value.Enabled == true, terminalAvailable), JsonSerializerOptions.Web)));
            }
            await SendAsync(new("configuration.request", Guid.Empty));
            while (!lifetime.IsCancellationRequested)
            {
                var message = await ReceiveAsync(socket, lifetime.Token);
                if (message is null) break;
                if (message.Type == "ping") await SendAsync(new("pong", message.Id));
                else if (message.Type == "work.output.applied" && long.TryParse(message.Data, out var sequence) &&
                    outputAcknowledgements.TryGetValue((message.Id, sequence), out var acknowledgement)) acknowledgement.TrySetResult();
                else if (message.Type == "configuration.begin" && message.Id == Guid.Empty && message.Configuration is { } config)
                    configuration.Begin(config);
                else if (message.Type == "configuration.deployment" && message.Deployment is { } deployment && message.Id == deployment.Id)
                    await configuration.ApplyAsync(deployment, lifetime.Token);
                else if (message.Type == "configuration.complete" && message.Id == Guid.Empty && message.Data is { Length: 64 } fingerprint &&
                    fingerprint.All(c => char.IsAsciiHexDigit(c)))
                {
                    await configuration.CompleteAsync(lifetime.Token);
                    await SendAsync(new("configuration.applied", Guid.Empty, Data: fingerprint));
                    state.Set(state.Snapshot with { Ready = true, CanExecuteWork = configuration.ExecutionEnabled });
                }
                else if (message.Type == "work.request" && message.Request is { } request && request.Id == message.Id)
                {
                    attachedOperations.Add(request.Id);
                    // No unbounded buffering and no concurrent mutating commands on this Agent.
                    if (work is { IsCompleted: false })
                        await SendAsync(new("work.response", request.Id, Response: new(request.Id, false, "agent_busy")));
                    else
                    {
                        var rejected = await jobStore.StartAsync(request);
                        if (rejected is not null) await SendAsync(new("work.response", request.Id, Response: rejected));
                        else work = PumpAsync(request.Id, 0);
                    }
                }
                else if (message.Type == "work.attach" && long.TryParse(message.Data, out var after) && after >= 0)
                {
                    attachedOperations.Add(message.Id);
                    if (work is { IsCompleted: false }) await SendAsync(new("work.response", message.Id, Response: new(message.Id, false, "agent_busy")));
                    else work = PumpAsync(message.Id, after);
                }
                else if (message.Type == "work.inspect")
                {
                    var inspection = await jobStore.InspectAsync(message.Id);
                    await SendAsync(new("work.inspection", message.Id, Job: inspection with { Response = inspection.Response is { } result ? result with { Output = null } : null }));
                }
                else if (message.Type == "work.cancel" && message.Data is "user" or "deadline" or "authorization_revoked")
                    await jobStore.CancelAsync(message.Id, message.Data);
                else if (message.Type == "work.revoke") await jobStore.CancelAllAsync("authorization_revoked");
                else if (message.Type is "terminal.open" or "terminal.input" or "terminal.resize" or "terminal.close")
                    await terminalManager.HandleAsync(message, SendAsync, lifetime.Token);
                else if (message.Type != "pong") throw new IOException("Unsupported Agent channel message.");
            }
        }
        catch (Exception error)
        {
            if (disconnectReason == "remote_close") disconnectReason = Describe(error);
            throw;
        }
        finally
        {
            state.Set(state.Snapshot with { Ready = false, CommandChannelAvailable = false, CanExecuteWork = false });
            lifetime.Cancel();
            socket.Abort();
            await terminalManager.CloseAllAsync();
            if (work is not null) await work;
            if (token.IsCancellationRequested) await jobStore.CancelAllAsync("shutdown");
            await jobStore.RecordDisconnectAsync(token.IsCancellationRequested ? "shutdown" : disconnectReason, (int?)socket.CloseStatus, attachedOperations);
        }
    }

    private static string Describe(Exception error) => error is WebSocketException webSocket
        ? $"WebSocketException:{webSocket.WebSocketErrorCode}" + (webSocket.InnerException is System.Net.Sockets.SocketException socket ? $":{socket.SocketErrorCode}" : "")
        : error.GetType().Name;

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
            if (part.MessageType != WebSocketMessageType.Text)
                throw new IOException("Invalid Agent frame.");
            stream.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) break;
        }
        var message = JsonSerializer.Deserialize<AgentWireMessage>(stream.GetBuffer().AsSpan(0, (int)stream.Length), JsonSerializerOptions.Web)
            ?? throw new IOException("Invalid Agent message.");
        if (message.Type is not { Length: > 0 and <= 64 }) throw new IOException("Invalid Agent message type.");
        if (message.Type.StartsWith("terminal.", StringComparison.Ordinal) && (stream.Length > 96 * 1024 || message.Response is not null ||
            message.Type != "terminal.open" && message.Request is not null || message.Type is "terminal.open" or "terminal.close" && message.Data is not null))
            throw new IOException("Invalid terminal frame.");
        return message;
    }
}
