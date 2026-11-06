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
        services.AddSingleton<IAgentOutputArchive>(sp => sp.GetRequiredService<AgentCommandBroker>());
        services.TryAddScoped<InstanceSettingsService>();
        services.TryAddScoped<AccessService>();
        services.TryAddScoped<OperationalPolicyService>();
        services.TryAddScoped<AgentPolicyService>();
        services.TryAddSingleton(new HttpTransportPolicy());
        services.AddScoped<AgentConfigurationSync>();
        return services;
    }

    public static IEndpointConventionBuilder MapStanzaAgentTransport(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/agent-channel", HandleAsync);

    private static async Task HandleAsync(HttpContext context, AgentCommandBroker broker,
        AgentAdministrationService administration, StanzaDbContext store, IHostEnvironment environment, TimeProvider clock, HttpTransportPolicy httpTransport)
    {
        if (!httpTransport.AllowsAgentRequest(context, environment))
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
                        await Task.Delay(LimitPolicy.Duration(snapshot.CheckIntervalSeconds), ct);
                    }
                });
            }
            finally { broker.Release(agent.Id, session); }
        }
        catch (AgentOperationException ex) when (!context.Response.HasStarted) { context.Response.StatusCode = ex.StatusCode; }
    }
}

public sealed class AgentCommandBroker(IServiceScopeFactory? scopes = null) : IAgentCommandDispatcher, IAgentJobDispatcher, IAgentWorkProgressSource, IAgentConnectionCatalog, IAgentOutputArchive
{
    private readonly ConcurrentDictionary<Guid, AgentDiscoveryAdmission> discoveryAdmission = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> outputWaiters = new();
    private void OutputAvailable(Guid id)
    {
        if (outputWaiters.TryRemove(id, out var waiter)) waiter.TrySetResult();
    }
    public async Task WaitForOutputAsync(Guid operationId, CancellationToken token = default)
    {
        var waiter = outputWaiters.GetOrAdd(operationId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        try { await waiter.Task.WaitAsync(TimeSpan.FromSeconds(1), token); } catch (TimeoutException) { }
        finally { outputWaiters.TryRemove(new KeyValuePair<Guid, TaskCompletionSource>(operationId, waiter)); }
    }
    public Task<IReadOnlyList<AgentOutputChunk>> ReadOutputAsync(Guid agentId, Guid operationId, long after, int count, CancellationToken token = default) =>
        sessions.TryGetValue(agentId, out var session) && session.Connected
            ? session.ReadOutputAsync(operationId, after, count, token) : throw new AgentUnavailableException();
    public Task CommitOutputAsync(Guid agentId, Guid operationId, long lastSequence, CancellationToken token = default) =>
        sessions.TryGetValue(agentId, out var session) && session.Connected
            ? session.CommitOutputAsync(operationId, lastSequence, token) : throw new AgentUnavailableException();
    public event Func<Guid, string, Task>? Progress;
    private async Task ReportAsync(Guid id, string stage)
    {
        if (Progress is not { } progress) return;
        foreach (Func<Guid, string, Task> handler in progress.GetInvocationList()) await handler(id, stage);
    }
    private readonly ConcurrentDictionary<Guid, AgentCommandSession> sessions = new();
    public IReadOnlyList<Guid> ConnectedAgentIds => sessions.Where(pair => pair.Value.Connected).Select(pair => pair.Key).ToArray();
    public bool IsConnected(Guid agentId) => sessions.TryGetValue(agentId, out var session) && session.Connected;
    public void Disconnect(Guid agentId) { if (sessions.TryGetValue(agentId, out var session)) session.Dispose(); }
    public AgentCapabilities? Capabilities(Guid agentId) => sessions.TryGetValue(agentId, out var session) && session.Connected ? session.Capabilities : null;
    public DateTimeOffset? ConfigurationSyncedAt(Guid agentId) => sessions.TryGetValue(agentId, out var session) && session.Connected ? session.ConfigurationSyncedAt : null;
    public bool ConfigurationPending(Guid agentId) => sessions.TryGetValue(agentId, out var session) && session.Connected && session.ConfigurationPending;
    public Task<Stanza.ControlPlane.Logs.IAgentLogSession> OpenLogsAsync(Guid agentId, Guid deploymentId,
        AgentExecution execution, AgentRuntimePolicy policy, CancellationToken token = default) =>
        sessions.TryGetValue(agentId, out var session) && session.Connected
            ? session.OpenLogsAsync(deploymentId, execution, policy, token) : throw new AgentUnavailableException();
    public AgentCommandSession? Reserve(Guid agentId)
    {
        var session = new AgentCommandSession(ReportAsync, async id =>
        {
            if (scopes is null) return 0;
            await using var scope = scopes.CreateAsyncScope();
            return await new Operations.OperationOutputStore(scope.ServiceProvider.GetRequiredService<StanzaDbContext>(),
                scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()).LastSequenceAsync(id);
        }, async diagnostic =>
        {
            if (scopes is null) return;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
            diagnostic.AgentId = agentId; db.Add(diagnostic); await db.SaveChangesAsync();
        }, OutputAvailable, async (job, ct) =>
        {
            if (scopes is null) return false;
            await using var scope = scopes.CreateAsyncScope();
            try { return await scope.ServiceProvider.GetRequiredService<Operations.OperationOutputStore>().CanReleaseAsync(agentId, job, ct); }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                scope.ServiceProvider.GetService<ILogger<AgentCommandBroker>>()?.LogWarning(error, "Archive acknowledgement for {OperationId} deferred; Agent output is retained.", job.Id);
                return false;
            }
        });
        return sessions.TryAdd(agentId, session) ? session : null;
    }
    public void Release(Guid agentId, AgentCommandSession session)
    {
        sessions.TryRemove(new KeyValuePair<Guid, AgentCommandSession>(agentId, session));
        session.Dispose();
    }
    public Task<AgentJobInspection> InspectAsync(Guid agentId, Guid operationId, CancellationToken token = default) =>
        sessions.TryGetValue(agentId, out var session) && session.Connected
            ? session.InspectAsync(operationId, token) : throw new AgentUnavailableException();
    public Task CancelAsync(Guid agentId, Guid operationId, string reason, CancellationToken token = default) =>
        sessions.TryGetValue(agentId, out var session) && session.Connected
            ? session.CancelAsync(operationId, reason, token) : Task.CompletedTask;
    public Task<AgentWorkResponse> AttachAsync(Guid agentId, Guid operationId, CancellationToken token = default) =>
        ExchangeAsync(agentId, operationId, null, token);
    public async Task<AgentWorkResponse> DispatchAsync(Guid agentId, AgentWorkRequest request, CancellationToken token = default)
    {
        if (!ExecutionScheduling.IsDiscovery(request.Operation)) return await ExchangeAsync(agentId, request.Id, request, token);
        var policy = request.Policy ?? new();
        if (!ExecutionScheduling.IsValid(policy)) return new(request.Id, false, "invalid_runtime_policy", ExecutionStopped: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(policy.DiscoveryTimeoutSeconds));
        var query = request;
        try
        {
            using var admission = await discoveryAdmission.GetOrAdd(agentId, _ => new()).EnterAsync(policy.MaximumParallelDiscovery, deadline.Token);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var response = await ExchangeAsync(agentId, query.Id, query, deadline.Token);
                if (deadline.IsCancellationRequested) return new(request.Id, false, token.IsCancellationRequested ? "discovery_cancelled" : "discovery_timeout");
                if (response.CancellationReason == "authorization_revoked") return response with { Id = request.Id };
                if (ExecutionScheduling.IsTransientDiscovery(request.Operation) && response.Code is "job_not_found" or "uncertain" or "discovery_cancelled" or "cancelled")
                    query = request with { Id = Guid.NewGuid() }; // Safe to retry a lost read-only query, never executable work.
                else if (response.Code != "agent_busy") return response with { Id = request.Id };
                // Busy is a pre-admission rejection, not a missing branch or an accepted job.
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
            }
        }
        catch (OperationCanceledException) { return new(request.Id, false, token.IsCancellationRequested ? "discovery_cancelled" : "discovery_timeout"); }
    }
    private async Task<AgentWorkResponse> ExchangeAsync(Guid agentId, Guid id, AgentWorkRequest? request, CancellationToken token)
    {
        if (!IsConnected(agentId)) throw new AgentUnavailableException();
        var first = true;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!sessions.TryGetValue(agentId, out var session) || !session.Connected)
                { await Task.Delay(250, token); continue; }
                var response = await session.ExchangeAsync(id, first ? request : null, token, request is not null && ExecutionScheduling.IsDiscovery(request.Operation));
                first = false;
                if (response.Code != "uncertain" || session.Connected || session.FailureReason is "authorization_revoked" or "protocol_error") return response;
                // Only attach by ID after a possibly delivered request. Never resend executable work.
                await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(id, false, "uncertain"); }
    }
}

public sealed class AgentCommandSession : IDisposable
{
    private readonly Func<Guid, string, Task>? report;
    private readonly ConcurrentDictionary<Guid, int> progressCount = new();
    private readonly Func<Guid, Task<long>>? cursor;
    private readonly Func<Operations.AgentDisconnectEntity, Task>? diagnostic;
    private readonly Action<Guid>? outputAvailable;
    private readonly Func<AgentJobInspection, CancellationToken, Task<bool>>? canArchive;
    private readonly ConcurrentDictionary<Guid, (AgentOutputRead Read, TaskCompletionSource<IReadOnlyList<AgentOutputChunk>> Completion)> outputReads = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentJobInspection>> inspections = new();
    private readonly ConcurrentDictionary<Guid, string> phases = new();
    private readonly ConcurrentDictionary<Guid, long> evidenceSequences = new();
    private long lastHeartbeat;
    public string? FailureReason { get; private set; }
    public AgentCommandSession(Func<Guid, string, Task>? report = null,
        Func<Guid, Task<long>>? cursor = null, Func<Operations.AgentDisconnectEntity, Task>? diagnostic = null,
        Action<Guid>? outputAvailable = null, Func<AgentJobInspection, CancellationToken, Task<bool>>? canArchive = null)
    { this.report = report; this.cursor = cursor; this.diagnostic = diagnostic; this.outputAvailable = outputAvailable; this.canArchive = canArchive; }
    // Wire JSON is never embedded in HTML; base64 must not inflate beyond its declared frame bound.
    private static readonly JsonSerializerOptions LogJson = new(JsonSerializerDefaults.Web)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly SemaphoreSlim send = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentWorkResponse>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, Stanza.ControlPlane.Logs.BrokerLogSession> logs = new();
    private readonly SemaphoreSlim logCapacity = new(16, 16);
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

    public async Task<IReadOnlyList<AgentOutputChunk>> ReadOutputAsync(Guid operationId, long after, int count, CancellationToken token)
    {
        if (!Connected) throw new AgentUnavailableException();
        if (operationId == Guid.Empty || after < 0 || count is < 1 or > 64) throw new ArgumentException("Invalid output read.");
        var query = Guid.NewGuid(); var read = new AgentOutputRead(operationId, after, count);
        var completion = new TaskCompletionSource<IReadOnlyList<AgentOutputChunk>>(TaskCreationOptions.RunContinuationsAsynchronously);
        outputReads[query] = (read, completion);
        try
        {
            await SendAsync(new("work.output.read", query, OutputRead: read), token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        }
        finally { outputReads.TryRemove(query, out _); }
    }
    public Task CommitOutputAsync(Guid operationId, long lastSequence, CancellationToken token) =>
        SendAsync(new("work.archive.committed", operationId, Data: lastSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)), token);

    public async Task<Stanza.ControlPlane.Logs.IAgentLogSession> OpenLogsAsync(Guid deploymentId,
        AgentExecution target, AgentRuntimePolicy policy, CancellationToken token)
    {
        if (!Connected || Capabilities is not { LogsAvailable: true }) throw new AgentUnavailableException();
        if (deploymentId == Guid.Empty || !policy.ExecutionEnabled || policy.TimeoutSeconds is < 0 or > 604800 || !AgentLogProtocol.IsValid(target)) throw new IOException("Invalid read-only log target or policy.");
        if (!await logCapacity.WaitAsync(0, token)) throw new IOException("Agent log stream limit reached.");
        var id = Guid.NewGuid();
        var session = new Stanza.ControlPlane.Logs.BrokerLogSession(id, SendAsync, key => { if (logs.TryRemove(key, out _)) logCapacity.Release(); });
        if (!logs.TryAdd(id, session)) { logCapacity.Release(); throw new IOException("Duplicate log identity."); }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            await SendAsync(new("log.open", id, new(id, deploymentId, "log.open", [], [], target, Policy: policy)), timeout.Token);
            await session.Opened.WaitAsync(timeout.Token);
            return session;
        }
        catch { await session.CloseAsync(CancellationToken.None); throw; }
    }

    public Task<AgentWorkResponse> DispatchAsync(AgentWorkRequest request, CancellationToken token) => ExchangeAsync(request.Id, request, token);
    public Task CancelAsync(Guid id, string reason, CancellationToken token) => SendAsync(new("work.cancel", id, Data: reason), token);
    public async Task<AgentJobInspection> InspectAsync(Guid id, CancellationToken token)
    {
        var completion = new TaskCompletionSource<AgentJobInspection>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inspections.TryAdd(id, completion)) throw new IOException("Inspection already in progress.");
        try
        {
            await SendAsync(new("work.inspect", id), token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        }
        finally { inspections.TryRemove(id, out _); }
    }
    public async Task<AgentWorkResponse> ExchangeAsync(Guid id, AgentWorkRequest? request, CancellationToken token, bool discovery = false)
    {
        if (id == Guid.Empty) return new(id, false, "invalid_request");
        discovery |= request is not null && ExecutionScheduling.IsDiscovery(request.Operation);
        var completion = new TaskCompletionSource<AgentWorkResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) return new(id, false, "duplicate_pending_request");
        var attempted = false;
        try
        {
            if (!Connected) throw new AgentUnavailableException();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            var after = cursor is null ? 0 : await cursor(id);
            evidenceSequences[id] = after;
            attempted = true;
            await SendAsync(request is not null ? new("work.request", id, request) : new("work.attach", id, Data: after.ToString(System.Globalization.CultureInfo.InvariantCulture)), linked.Token);
            var response = await completion.Task.WaitAsync(linked.Token);
            if (Connected) evidenceSequences.TryRemove(id, out _);
            return response;
        }
        catch (Exception ex) when (attempted && ex is OperationCanceledException or WebSocketException or IOException)
        {
            if (discovery && ex is OperationCanceledException && token.IsCancellationRequested && Connected)
            {
                try { await SendAsync(new("work.cancel", id, Data: "user"), CancellationToken.None); } catch (Exception) { }
                evidenceSequences.TryRemove(id, out _);
                return new(id, false, "discovery_cancelled");
            }
            // Once a send is attempted, a missing acknowledgement is not evidence that execution did not occur.
            // Only the transport is disposed; the Agent owns the job lifetime.
            Dispose();
            return new(id, false, "uncertain");
        }
        finally { pending.TryRemove(id, out _); progressCount.TryRemove(id, out _); }
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
                Interlocked.Exchange(ref lastHeartbeat, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
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
                else if (message.Type == "work.inspection" && message.Job is { } job && job.Id == message.Id)
                {
                    if (inspections.TryGetValue(message.Id, out var inspection)) inspection.TrySetResult(job);
                }
                else if (message.Type == "work.response" && message.Response is { } response && response.Id == message.Id)
                {
                    if (response.Code is not { Length: > 0 and <= 100 } ||
                        !response.Code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ||
                        response.ResolvedSources?.Count > 32 || response.ResolvedImages?.Count > 10000 || response.LastOutputSequence < 0)
                        throw new IOException("Invalid Agent response.");
                    if (pending.TryGetValue(message.Id, out var completion)) completion.TrySetResult(response);
                    outputAvailable?.Invoke(message.Id);
                }
                else if (message.Type == "work.output.available" && message.Id != Guid.Empty && long.TryParse(message.Data, out var available) && available >= 0)
                    outputAvailable?.Invoke(message.Id);
                else if (message.Type == "work.output.batch" && outputReads.TryGetValue(message.Id, out var read) && message.OutputRead == read.Read && message.OutputBatch is { } batch)
                {
                    if (batch.Count > read.Read.Count || batch.Where((chunk, index) => chunk is null || chunk.Sequence != read.Read.After + index + 1 ||
                        chunk.Stream is not ("stdout" or "stderr" or "system") || chunk.Text is not { Length: > 0 and <= 16384 }).Any())
                        throw new IOException("Invalid output batch.");
                    read.Completion.TrySetResult(batch);
                }
                else if (message.Type == "work.output.batch" && !outputReads.ContainsKey(message.Id)) { /* A cancelled reader may have an in-flight reply. */ }
                else if (message.Type == "work.archive.pending" && message.Job is { Stopped: true, Response: not null } archived && archived.Id == message.Id)
                {
                    if (canArchive is not null && await canArchive(archived, linked.Token))
                        await CommitOutputAsync(archived.Id, archived.LastSequence, linked.Token);
                }
                else if (message.Type == "work.progress")
                {
                    if (!pending.ContainsKey(message.Id)) continue;
                    if (message.Data is not ("Validating" or "Preparing" or "Building" or "Pulling" or "Applying" or "Checking") || progressCount.AddOrUpdate(message.Id, 1, (_, count) => count + 1) > 32) throw new IOException("Invalid operation progress.");
                    phases[message.Id] = message.Data;
                    if (report is not null) await report(message.Id, message.Data);
                }
                else if (message.Type == "capabilities")
                {
                    if (message.Id != Guid.Empty || message.Data is not { Length: > 0 and <= 2048 }) throw new IOException("Invalid capabilities.");
                    var caps = JsonSerializer.Deserialize<AgentCapabilities>(message.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 });
                    static bool Safe(string? value, int max) => value is null || value.Length is > 0 && value.Length <= max && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_');
                    if (caps is null || caps.Runtime != "docker" || caps.Architecture is null || !Safe(caps.Architecture, 32) ||
                        !Safe(caps.DockerVersion, 64) || !Safe(caps.ComposeVersion, 64) || caps.LogsAvailable && !caps.DockerAvailable)
                        throw new IOException("Invalid capabilities.");
                    Capabilities = caps;
                }
                else if (message.Type is "log.opened" or "log.output" or "log.ended")
                {
                    if (message.Id == Guid.Empty || message.Data?.Length > 87384) throw new IOException("Invalid log frame.");
                    if (logs.TryGetValue(message.Id, out var log) && !log.Receive(message))
                    {
                        // A slow viewer closes its log subscription only, not the Agent command channel.
                        _ = log.CloseAsync(linked.Token).AsTask();
                    }
                }
                else if (message.Type == "ping") await SendAsync(new("pong", message.Id), linked.Token);
                else if (message.Type != "pong") throw new IOException("Unsupported Agent message.");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or JsonException or FormatException)
        {
            FailureReason ??= ex switch
            {
                WebSocketException webSocket => $"WebSocketException:{webSocket.WebSocketErrorCode}" +
                    (webSocket.InnerException is System.Net.Sockets.SocketException socketError ? $":{socketError.SocketErrorCode}" : ""),
                JsonException or FormatException or IOException => "protocol_error",
                OperationCanceledException => token.IsCancellationRequested ? "shutdown" : "transport_timeout_or_close",
                _ => ex.GetType().Name
            };
        }
        finally
        {
            var diagnostics = evidenceSequences.Select(pair => new Operations.AgentDisconnectEntity { Id = Guid.NewGuid(), OperationId = pair.Key,
                At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Reason = FailureReason ?? (token.IsCancellationRequested ? "shutdown" : "remote_close"),
                CloseStatus = (int?)socket.CloseStatus, Phase = phases.GetValueOrDefault(pair.Key) ?? "unknown",
                LastHeartbeat = Interlocked.Read(ref lastHeartbeat), AcknowledgedSequence = pair.Value }).ToArray();
            linked.Cancel();
            Dispose();
            if (diagnostic is not null) foreach (var item in diagnostics) await diagnostic(item);
            try { await monitor; } catch (OperationCanceledException) { }
            if (synchronization is not null) await synchronization;
        }
        async Task SynchronizeAsync()
        {
            try { await synchronize!(SendAsync, hash => expectedConfiguration = hash, linked.Token); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception ex)
            {
                FailureReason = "configuration_" + ex.GetType().Name;
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
                if (!await validate(token))
                {
                    FailureReason = "authorization_revoked";
                    await SendAsync(new("work.revoke", Guid.Empty), token);
                    Dispose(); return;
                }
                await SendAsync(new("ping", Guid.NewGuid()), token);
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { FailureReason = "heartbeat_" + ex.GetType().Name; Dispose(); }
    }

    private async Task SendAsync(AgentWireMessage message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, message.Type.StartsWith("log.", StringComparison.Ordinal) ? LogJson : JsonSerializerOptions.Web);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await send.WaitAsync(timeout.Token);
        try
        {
            for (var offset = 0; offset < bytes.Length; offset += 16384)
            {
                var count = Math.Min(16384, bytes.Length - offset);
                await socket!.SendAsync(bytes.AsMemory(offset, count), WebSocketMessageType.Text, offset + count == bytes.Length, timeout.Token);
            }
        }
        finally { send.Release(); }
    }
    public void Dispose()
    {
        Volatile.Write(ref ready, 0);
        lifetime.Cancel();
        socket?.Abort();
        Capabilities = null;
        foreach (var log in logs.Values) log.Complete();
        foreach (var pair in pending) pair.Value.TrySetResult(new(pair.Key, false, "uncertain"));
        foreach (var pair in inspections) pair.Value.TrySetException(new AgentUnavailableException());
        foreach (var pair in outputReads) pair.Value.Completion.TrySetException(new AgentUnavailableException());
    }
}

public static class AgentSocketFrames
{
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
        if (message.Type.StartsWith("log.", StringComparison.Ordinal) &&
            (stream.Length > 96 * 1024 || message.Request is not null || message.Response is not null || message.Type == "log.opened" && message.Data is not null))
            throw new IOException("Invalid log frame.");
        return message;
    }
}
