using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Terminals;

/// <summary>Ephemeral sessions: never enter the durable command ledger or use host execution.</summary>
public sealed class AgentTerminalManager
{
    public const int MaximumSessions = 16;
    public const int MaximumChunk = 65536;
    private readonly ConcurrentDictionary<Guid, Session> sessions = new();
    private readonly SemaphoreSlim capacity = new(MaximumSessions, MaximumSessions);

    public async Task HandleAsync(AgentWireMessage message, Func<AgentWireMessage, Task> send, CancellationToken token)
    {
        if (message.Id == Guid.Empty) throw new IOException("Invalid terminal session.");
        if (message.Type == "terminal.open")
        {
            if (message.Request is not { } request || request.Id != message.Id || request.Operation != "terminal.open" ||
                request.Policy is not { ExecutionEnabled: true } || request.Execution is null || request.Files is not { Count: 0 } ||
                request.EnvironmentFiles is not { Count: 0 } || request.Sources?.Count > 0 || request.Registries?.Count > 0 ||
                JsonSerializer.SerializeToUtf8Bytes(request, JsonSerializerOptions.Web).Length > 40 * 1024)
                throw new IOException("Invalid terminal open.");
            if (!await capacity.WaitAsync(0, token)) { await send(new("terminal.exited", message.Id, Data: "125")); return; }
            var session = new Session(request, send, token);
            if (!sessions.TryAdd(message.Id, session)) { capacity.Release(); session.Dispose(); throw new IOException("Duplicate terminal session."); }
            session.Work = RunAsync();
            async Task RunAsync()
            {
                try { await session.RunAsync(); }
                finally { sessions.TryRemove(message.Id, out _); session.Dispose(); capacity.Release(); }
            }
            return;
        }
        // Late in-flight frames after a close are deliberately ignored; they cannot open/replay a session.
        if (!sessions.TryGetValue(message.Id, out var current)) return;
        if (message.Type == "terminal.close") current.Cancel();
        else if (message.Type == "terminal.input")
        {
            if (message.Data is not { Length: > 0 and <= 87384 }) throw new IOException("Invalid terminal input.");
            var bytes = Convert.FromBase64String(message.Data);
            if (bytes.Length is 0 or > MaximumChunk) throw new IOException("Invalid terminal input.");
            try { await current.InputAsync(bytes, token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { current.Cancel(); }
        }
        else if (message.Type == "terminal.resize")
        {
            if (message.Data is not { Length: <= 64 }) throw new IOException("Invalid terminal resize.");
            var dimensions = JsonSerializer.Deserialize<int[]>(message.Data);
            if (dimensions is not { Length: 2 } || dimensions[0] is < 2 or > 500 || dimensions[1] is < 1 or > 300)
                throw new IOException("Invalid terminal resize.");
            try { await current.ResizeAsync(dimensions[0], dimensions[1], token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException or ObjectDisposedException) { current.Cancel(); }
        }
        else throw new IOException("Unsupported terminal frame.");
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
        private sealed record InputCommand(byte[]? Data, int Columns = 0, int Rows = 0);
        private readonly Channel<InputCommand> input = Channel.CreateBounded<InputCommand>(new BoundedChannelOptions(16)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        private DockerTerminalClient? client;
        private Stream? stream;
        private string? container;
        private string? exec;
        private int pid;
        public Task? Work { get; set; }
        public Session(AgentWorkRequest request, Func<AgentWireMessage, Task> send, CancellationToken token)
        {
            this.request = request; this.send = send;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            lifetime.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.Policy!.TimeoutSeconds, 1, 3600)));
        }
        public void Cancel() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
        public async Task RunAsync()
        {
            var exit = 125;
            Task? inputPump = null;
            try
            {
                var execution = request.Execution!;
                Validate(execution);
                client = new(request.Policy!.DockerEndpoint);
                using var opening = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                opening.CancelAfter(TimeSpan.FromSeconds(20));
                container = await client.ResolveAsync(request.DeploymentId, execution, opening.Token);
                // Fixed launcher, never interpolated. runc's terminal session leader keeps this PID across exec.
                var command = new List<string> { "/bin/sh", "-c", "printf '\\036STANZA_PID:%s\\037' \"$$\"; exec \"$@\"", "stanza-terminal", execution.Executable };
                command.AddRange(execution.Arguments);
                exec = await client.CreateAsync(container, new { AttachStdin = true, AttachStdout = true, AttachStderr = true,
                    Tty = true, Cmd = command, User = execution.User ?? "", WorkingDir = execution.WorkingDirectory ?? "",
                    Env = execution.Environment?.Select(x => x.Key + "=" + x.Value).ToArray() ?? [] }, opening.Token);
                stream = await client.StartAsync(exec, opening.Token);
                var marker = new List<byte>(); var one = new byte[1];
                while (marker.Count < 64)
                {
                    if (await stream.ReadAsync(one, opening.Token) == 0) throw new IOException("Terminal launcher unavailable.");
                    marker.Add(one[0]); if (one[0] == 31) break;
                }
                var text = Encoding.ASCII.GetString(marker.ToArray());
                const string prefix = "\u001eSTANZA_PID:";
                if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith('\u001f') ||
                    !int.TryParse(text[prefix.Length..^1], out pid) || pid <= 1) throw new IOException("Invalid terminal launcher.");
                await send(new("terminal.opened", request.Id));
                inputPump = PumpInputAsync();
                var buffer = new byte[MaximumChunk];
                int count;
                while ((count = await stream.ReadAsync(buffer, lifetime.Token)) != 0)
                    await send(new("terminal.output", request.Id, Data: Convert.ToBase64String(buffer, 0, count)));
                using var inspectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var inspect = await client.JsonAsync(HttpMethod.Get, "exec/" + exec + "/json", null, inspectTimeout.Token);
                if (!inspect.RootElement.GetProperty("Running").GetBoolean()) exit = inspect.RootElement.GetProperty("ExitCode").GetInt32();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or System.Net.WebSockets.WebSocketException) { }
            finally
            {
                Cancel();
                input.Writer.TryComplete();
                if (inputPump is not null) await inputPump;
                await TerminateAsync();
                try { await send(new("terminal.exited", request.Id, Data: exit.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
                catch (Exception) { /* The authenticated channel may already be gone. */ }
            }
        }
        public Task InputAsync(byte[] bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!input.Writer.TryWrite(new(bytes))) Cancel();
            return Task.CompletedTask;
        }
        public Task ResizeAsync(int columns, int rows, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!input.Writer.TryWrite(new(null, columns, rows))) Cancel();
            return Task.CompletedTask;
        }
        private async Task PumpInputAsync()
        {
            try
            {
                await foreach (var command in input.Reader.ReadAllAsync(lifetime.Token))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    if (command.Data is not null) await stream!.WriteAsync(command.Data, timeout.Token);
                    else await client!.PostAsync($"exec/{exec}/resize?h={command.Rows}&w={command.Columns}", null, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException or ObjectDisposedException) { Cancel(); }
        }
        private async Task TerminateAsync()
        {
            // Closing a Docker attach alone does not guarantee exit. Kill only the exec's namespace process group.
            if (client is not null && exec is not null && container is not null && pid > 1)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var state = await client.JsonAsync(HttpMethod.Get, "exec/" + exec + "/json", null, timeout.Token);
                    if (state.RootElement.GetProperty("Running").GetBoolean())
                    {
                        var verified = await client.ResolveAsync(request.DeploymentId, request.Execution! with { Service = null, Container = container }, timeout.Token);
                        if (verified != container) throw new IOException("Container changed.");
                        var killer = await client.CreateAsync(container, new { AttachStdout = true, AttachStderr = true, Tty = true,
                            User = request.Execution.User ?? "", Cmd = new[] { "/bin/sh", "-c", "kill -KILL \"-$1\" 2>/dev/null || kill -KILL \"$1\"", "stanza-close", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, timeout.Token);
                        await using var killStream = await client.StartAsync(killer, timeout.Token);
                        var discard = new byte[1024];
                        while (await killStream.ReadAsync(discard, timeout.Token) != 0) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException) { }
            }
            if (stream is not null) await stream.DisposeAsync();
        }
        private static void Validate(AgentExecution e)
        {
            if (string.IsNullOrWhiteSpace(e.Executable) || e.Executable.Length > 1024 || e.Executable.Any(char.IsControl) ||
                e.Arguments is null || e.Arguments.Count > 64 || e.Arguments.Any(x => x is null || x.Length > 4096 || x.Contains('\0')) ||
                e.Arguments.Sum(x => x.Length) > 8192 || e.WorkingDirectory is not null && (!e.WorkingDirectory.StartsWith('/') || e.WorkingDirectory.Length > 1024 || e.WorkingDirectory.Any(char.IsControl)) ||
                e.User is not null && (e.User.Length > 128 || e.User.Any(char.IsControl)) ||
                e.Environment is not null && (e.Environment.Count > 64 || e.Environment.Any(x => x.Key.Length is 0 or > 128 ||
                    !x.Key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || x.Value is null || x.Value.Length > 4096 || x.Value.Contains('\0')) || e.Environment.Sum(x => x.Key.Length + x.Value.Length) > 16384))
                throw new IOException("Invalid terminal execution.");
        }
        public void Dispose() { while (input.Reader.TryRead(out _)) { } client?.Dispose(); lifetime.Dispose(); }
    }
}
