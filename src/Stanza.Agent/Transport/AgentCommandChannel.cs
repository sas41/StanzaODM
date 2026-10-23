using System.Net.WebSockets;
using System.Text.Json;
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
    IOptions<HostExecutionOptions>? hostOptions = null, IHostEnvironment? environment = null) : IAgentCommandChannel
{
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
        var terminalManager = terminals ?? new AgentTerminalManager();
        // Per-connection state: an interrupted replication always starts again with a full snapshot.
        var configuration = new AgentConfigurationCache(options);
        async Task SendAsync(AgentWireMessage message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, message.Type.StartsWith("terminal.", StringComparison.Ordinal) ? TerminalJson : JsonSerializerOptions.Web);
            if (bytes.Length > 32 * 1024 * 1024) throw new IOException("Agent frame exceeds the limit.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await send.WaitAsync(timeout.Token);
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token); }
            finally { send.Release(); }
        }
        async Task ExecuteAsync(AgentWorkRequest request)
        {
            async Task Report(Guid id, string stage) => await SendAsync(new("work.progress", id, Data: stage));
            var reporter = executor as IReportingAgentWorkExecutor;
            if (reporter is not null) reporter.Progress += Report;
            try
            {
                var response = await executor.ExecuteAsync(request, lifetime.Token);
                await SendAsync(new("work.response", response.Id, Response: response));
            }
            catch (Exception) { lifetime.Cancel(); socket.Abort(); }
            finally { if (reporter is not null) reporter.Progress -= Report; }
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
                    // No unbounded buffering and no concurrent mutating commands on this Agent.
                    if (work is { IsCompleted: false })
                        await SendAsync(new("work.response", request.Id, Response: new(request.Id, false, "agent_busy")));
                    else work = ExecuteAsync(request);
                }
                else if (message.Type is "terminal.open" or "terminal.input" or "terminal.resize" or "terminal.close")
                    await terminalManager.HandleAsync(message, SendAsync, lifetime.Token);
                else if (message.Type != "pong") throw new IOException("Unsupported Agent channel message.");
            }
        }
        finally
        {
            state.Set(state.Snapshot with { Ready = false, CommandChannelAvailable = false, CanExecuteWork = false });
            lifetime.Cancel();
            socket.Abort();
            await terminalManager.CloseAllAsync();
            if (work is not null) await work;
        }
    }

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
            if (part.MessageType != WebSocketMessageType.Text || stream.Length + part.Count > 32 * 1024 * 1024)
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
