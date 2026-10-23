using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Automation;

namespace Stanza.ControlPlane.Operations;

public sealed class OperationException(string message) : Exception(message);
public sealed record OperationDetail(OperationSummary Summary, string? Output, bool OutputAllowed = true)
{
    public override string ToString() => $"OperationDetail {{ Id = {Summary.Id}, Output = [REDACTED] }}";
}

public sealed class OperationService(StanzaDbContext db, DeploymentDraftService drafts, AccessService access,
    InstanceSettingsService settings, OperationalPolicyService policies, ISourceSnapshotProvider sources,
    IDataProtectionProvider protection, TimeProvider clock, AuditService audit, AgentPolicyService agentPolicies, InstanceMaintenance? maintenance = null, AutomationOperationGuard? automationGuard = null) : IOperationService
{
    private static readonly IReadOnlyDictionary<string, string> Permissions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["validate"] = "deployment.validate", ["build"] = "deployment.build", ["pull"] = "deployment.deploy",
        ["deploy"] = "deployment.deploy", ["start"] = "deployment.start", ["stop"] = "deployment.stop",
        ["restart"] = "deployment.restart", ["remove"] = "deployment.remove", ["status"] = "deployment.read",
        ["logs"] = "logs.read", ["container-exec"] = "container.execute", ["image-digests"] = "automation.read",
        ["environment-preview"] = "deployment.read"
    };
    public static bool Terminal(string state) => state is "Succeeded" or "Failed" or "Cancelled" or "Unknown";
    internal static bool ControlsExistingContainers(string kind) => kind is "start" or "stop" or "restart" or "remove" or "status" or "logs" or "container-exec";
    public async Task<OperationSummary> EnqueueAsync(OperationSubmission submission, CancellationToken token = default)
    {
        if (maintenance?.Paused == true) throw new OperationException("Recovery requires a Control Plane restart before execution can resume.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await SecurityWriteGuard.LockAsync(db, token);
        if (automationGuard is not null)
        {
            try { await automationGuard.ValidateSubmissionAsync(submission, token); }
            catch (AutomationAccessException) { throw new OperationException("The automation submission is not authorized."); }
        }
        else if (submission.SourceOverrides?.Count > 0 || submission.ImageOverrides?.Count > 0)
            throw new OperationException("Action input authorization is unavailable.");
        if (submission.Kind == "host-exec" && !(await policies.GetAsync(token)).Policy.AllowHostActions)
            throw new OperationException("Host actions are disabled in operational policy. Host namespace prerequisites are also required on the Agent.");
        var id = Guid.NewGuid();
        var files = new List<AgentFile>();
        var environments = new List<AgentEnvironmentFile>();
        var sourceSnapshot = new DeploymentSourcesSnapshot([], []);
        var imageOverrides = submission.ImageOverrides;
        var actionSelections = automationGuard is not null ? await automationGuard.AuthoredBindingsAsync(submission.Trigger, submission.DeploymentId, submission.Kind, token) : null;
        Guid? parentAppliedOperationId = null;
        long revision = 0;
        Guid? target = submission.AgentId;
        if (submission.DeploymentId is { } deploymentId)
        {
            await AuthorizeAsync(submission.ActorId, deploymentId, submission.Kind, token);
            var snapshot = await drafts.SnapshotForExecutionAsync(submission.ActorId, deploymentId, ct: token);
            var runtime = await db.Set<DeploymentRuntimeEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deploymentId, token);
            revision = ControlsExistingContainers(submission.Kind) ? runtime?.Revision ?? 0 : snapshot.Revision;
            target ??= ControlsExistingContainers(submission.Kind) && runtime is not null ? runtime.AgentId : snapshot.AgentId;
            if (!ControlsExistingContainers(submission.Kind))
            {
                files.AddRange(snapshot.Files);
                var inherited = await settings.ReadInternalAsync(token);
                var agentEnvironment = "";
                if (target is { } environmentAgent)
                {
                    var agent = await db.Set<AgentEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == environmentAgent, token)
                        ?? throw new OperationException("The target Agent does not exist.");
                    agentEnvironment = protection.CreateProtector("Stanza.AgentEnvironment.v1").CreateProtector(environmentAgent.ToString("N")).Unprotect(agent.ProtectedEnvironment);
                }
                environments.Add(new("global.env", inherited.GlobalEnvironment));
                environments.Add(new("agent.env", agentEnvironment));
                environments.Add(new("deployment.env", snapshot.EnvironmentText));
                if (submission.Kind != "environment-preview") sourceSnapshot = await sources.PrepareAsync(submission.ActorId, deploymentId, token);
            }
        }
        else if (submission.Kind != "host-exec") throw new OperationException("A deployment is required for this operation.");
        if (target is null || target == Guid.Empty) throw new OperationException("Assign an approved Agent before running this operation.");
        if (submission.Kind == "host-exec") await RequireAsync(submission.ActorId, "host.execute", "agent", target, token);
        else await RequireAsync(submission.ActorId, "agent.read", "agent", target, token);
        if (!await db.Set<AgentEntity>().AnyAsync(x => x.Id == target && x.Status == "approved", token))
            throw new OperationException("The target Agent is not approved.");
        if (submission.AgentId.HasValue && submission.DeploymentId.HasValue)
            await RequireAsync(submission.ActorId, "deployment.move", "deployment", submission.DeploymentId, token);
        if (submission.Kind is "host-exec" or "container-exec" && submission.Execution is null)
            throw new OperationException("An executable and argument list are required.");
        if (submission.Execution is { } execution && (string.IsNullOrWhiteSpace(execution.Executable) || execution.Executable.Contains('\0') || execution.Arguments.Count > 256 || execution.Arguments.Any(x => x is null || x.Length > 8192 || x.Contains('\0'))))
            throw new OperationException("Invalid command arguments.");
        if (submission.Trigger.Length is < 1 or > 100 || submission.Trigger.Any(char.IsControl)) throw new OperationException("Invalid operation trigger.");
        if (submission.Kind == "environment-preview")
            await RequireEnvironmentPreviewAsync(submission.ActorId, submission.DeploymentId!.Value, target.Value, token);
        if (await IsImagePolicyAsync(submission.ActorId, submission.Trigger, submission.Kind, submission.DeploymentId, token))
        {
            if (automationGuard is null) throw new OperationException("Image-policy execution authorization is unavailable.");
            await automationGuard.AuthorizeAsync(submission.ActorId, submission.Trigger, submission.Kind, submission.DeploymentId, target.Value, token);
            var baseline = await ImageBaselineAsync(submission.ActorId, submission.DeploymentId!.Value, target.Value, sourceSnapshot, null, null, token);
            parentAppliedOperationId = baseline.Operation.Id;
            sourceSnapshot = baseline.Sources;
            imageOverrides = baseline.Request.ImageOverrides;
            actionSelections = ReadActionSelections(baseline.Operation);
        }
        sourceSnapshot = ApplySourceOverrides(sourceSnapshot, submission.SourceOverrides);
        var request = new AgentWorkRequest(id, submission.DeploymentId ?? Guid.Empty, submission.Kind, files, environments,
            submission.Execution, sourceSnapshot.Git, sourceSnapshot.Registries, await agentPolicies.RuntimeAsync(target.Value, token),
            ImageOverrides: imageOverrides, SourceOverrides: submission.SourceOverrides);
        var serialized = JsonSerializer.SerializeToNode(request)!;
        if (actionSelections is not null) serialized["_stanzaActionSelections"] = JsonSerializer.SerializeToNode(actionSelections);
        if (submission.DeploymentId is { } sourceDeployment && !ControlsExistingContainers(submission.Kind) && submission.Kind != "environment-preview")
            serialized["_stanzaSourceDefinitions"] = JsonSerializer.SerializeToNode(await SourceDefinitionsAsync(sourceDeployment, token));
        var requestText = serialized.ToJsonString();
        var operation = new OperationEntity { Id = id, ActorId = submission.ActorId, DeploymentId = submission.DeploymentId,
            RequiresMovePermission = submission.AgentId.HasValue && submission.DeploymentId.HasValue,
            ParentAppliedOperationId = parentAppliedOperationId,
            AgentId = target.Value, Revision = revision, Kind = submission.Kind, Trigger = submission.Trigger,
            CreatedAt = clock.GetUtcNow().ToUnixTimeSeconds(), ProtectedRequest = Protector(id).Protect(requestText) };
        db.Set<OperationEntity>().Add(operation);
        try { await db.SaveChangesAsync(token); await transaction.CommitAsync(token); }
        finally { db.Entry(operation).State = EntityState.Detached; }
        await audit.WriteAsync(submission.ActorId, "operation.queued", id.ToString(), submission.Kind, token);
        return Summary(operation);
    }

    public async Task<OperationSummary> WaitAsync(Guid actorId, Guid operationId, CancellationToken token = default)
    {
        while (true)
        {
            var detail = await DetailAsync(actorId, operationId, token);
            if (Terminal(detail.Summary.State)) return detail.Summary;
            await Task.Delay(TimeSpan.FromMilliseconds(250), clock, token);
        }
    }
    public async Task<IReadOnlyList<OperationSummary>> ListAsync(Guid actor, Guid? deploymentId = null, CancellationToken ct = default)
    {
        var query = db.Set<OperationEntity>().AsNoTracking();
        if (deploymentId.HasValue) query = query.Where(x => x.DeploymentId == deploymentId);
        var rows = await query.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(ct);
        var result = new List<OperationSummary>();
        foreach (var row in rows) if (await CanReadAsync(actor, row, ct)) result.Add(Summary(row));
        return result;
    }
    public async Task<OperationDetail> DetailAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        var row = await RequiredAsync(id, ct);
        if (!await CanReadAsync(actor, row, ct)) throw new OperationException("Operation read access is denied.");
        var allowed = await CanReadOutputAsync(actor, row, ct);
        var output = row.ProtectedResponse is not null && allowed
            ? JsonSerializer.Deserialize<AgentWorkResponse>(Protector(id).Unprotect(row.ProtectedResponse))?.Output : null;
        return new(Summary(row), output, allowed);
    }

    public async Task<bool> CanReadOutputAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        // Authorization checks must not load large encrypted execution snapshots on every live-feed tick.
        var row = await db.Set<OperationEntity>().AsNoTracking().Where(x => x.Id == id)
            .Select(x => new OperationEntity { Id = x.Id, Kind = x.Kind, DeploymentId = x.DeploymentId, AgentId = x.AgentId }).SingleOrDefaultAsync(ct)
            ?? throw new OperationException("Operation not found.");
        return await CanReadAsync(actor, row, ct) && await CanReadOutputAsync(actor, row, ct);
    }

    private async Task<bool> CanReadOutputAsync(Guid actor, OperationEntity row, CancellationToken ct) =>
        row.Kind == "environment-preview" && row.DeploymentId is { } previewDeployment
            ? await CanRevealEnvironmentPreviewAsync(actor, previewDeployment, row.AgentId, ct)
            : row.DeploymentId is { } deployment ? await access.IsAllowedAsync(actor, "logs.read", "deployment", deployment, ct)
            : await access.IsAllowedAsync(actor, "host.execute", "agent", row.AgentId, ct);
    public async Task CancelAsync(Guid actorId, Guid operationId, CancellationToken token = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await SecurityWriteGuard.LockAsync(db, token);
        var row = await RequiredAsync(operationId, token);
        if (row.DeploymentId is { } deployment) await RequireAsync(actorId, "operations.cancel", "deployment", deployment, token);
        else await RequireAsync(actorId, "host.execute", "agent", row.AgentId, token);
        if (Terminal(row.State)) throw new OperationException("The operation has already finished.");
        var queued = row.State == "Queued";
        await db.Set<OperationEntity>().Where(x => x.Id == row.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, queued ? "Cancelled" : "CancellationRequested")
            .SetProperty(x => x.CompletedAt, queued ? clock.GetUtcNow().ToUnixTimeSeconds() : (long?)null), token);
        await transaction.CommitAsync(token);
        await audit.WriteAsync(actorId, "operation.cancel", operationId.ToString(), queued ? "cancelled_before_dispatch" : "requested", token);
    }
    public async Task ReconcileAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        var row = await RequiredAsync(id, ct);
        if (row.DeploymentId is not { } deployment || row.State != "Unknown") throw new OperationException("Only uncertain deployment operations can be reconciled.");
        await RequireAsync(actor, "operations.cancel", "deployment", deployment, ct);
        var uncertaintyAt = row.CompletedAt ?? clock.GetUtcNow().ToUnixTimeSeconds();
        if (!await db.Set<OperationEntity>().AnyAsync(x => x.DeploymentId == deployment && x.AgentId == row.AgentId && x.Kind == "status" && x.State == "Succeeded" && x.CreatedAt >= uncertaintyAt && x.CompletedAt >= uncertaintyAt, ct))
            throw new OperationException("Run a fresh successful status inspection on the same Agent before acknowledging an uncertain outcome.");
        await db.Set<OperationEntity>().Where(x => x.Id == id && x.State == "Unknown").ExecuteUpdateAsync(update => update.SetProperty(x => x.State, "Failed").SetProperty(x => x.Code, "reconciled_not_retried"), ct);
        await transaction.CommitAsync(ct);
        await audit.WriteAsync(actor, "operation.reconciled", id.ToString(), "not_retried", ct);
    }
    internal async Task AuthorizeExecutionAsync(OperationEntity row, CancellationToken ct)
    {
        if (maintenance?.Paused == true) throw new OperationException("Execution is paused for instance recovery.");
        if (row.DeploymentId is { } deployment)
        {
            await AuthorizeAsync(row.ActorId, deployment, row.Kind, ct);
            if (row.RequiresMovePermission) await RequireAsync(row.ActorId, "deployment.move", "deployment", deployment, ct);
            var current = ControlsExistingContainers(row.Kind) || row.Kind == "environment-preview"
                ? new DeploymentSourcesSnapshot([], []) : await sources.PrepareAsync(row.ActorId, deployment, ct);
            var captured = Request(row);
            if (automationGuard is not null) await automationGuard.ValidateSubmissionAsync(new(row.ActorId, row.DeploymentId, row.Kind, row.Trigger,
                row.RequiresMovePermission || row.Kind == "host-exec" ? row.AgentId : null, captured.Execution, captured.SourceOverrides, captured.ImageOverrides), ct);
            current = ApplySourceOverrides(current, captured.SourceOverrides);
            var imagePolicy = await IsImagePolicyAsync(row.ActorId, row.Trigger, row.Kind, deployment, ct);
            if (imagePolicy != row.ParentAppliedOperationId.HasValue) throw new OperationException("The image-policy baseline is invalid.");
            if (imagePolicy)
            {
                if (automationGuard is null) throw new OperationException("Image-policy execution authorization is unavailable.");
                var baseline = await ImageBaselineAsync(row.ActorId, deployment, row.AgentId, current, row.ParentAppliedOperationId, row.Id, ct);
                if (row.Revision != baseline.Operation.Revision || !SameFiles(captured.Files, baseline.Request.Files) ||
                    !SameEnvironment(captured.EnvironmentFiles, baseline.Request.EnvironmentFiles) ||
                    JsonSerializer.Serialize(captured.ImageOverrides) != JsonSerializer.Serialize(baseline.Request.ImageOverrides))
                    throw new OperationException("The applied image-policy configuration changed; create a new operation snapshot.");
                current = baseline.Sources;
            }
            if (!SameSources(current, captured))
                throw new OperationException("Source credentials or definitions changed; create a new operation snapshot.");
            if (!ControlsExistingContainers(row.Kind) && row.Kind != "environment-preview" &&
                JsonSerializer.Serialize(await SourceDefinitionsAsync(deployment, ct)) != JsonSerializer.Serialize(ReadSourceDefinitions(row)))
                throw new OperationException("Source bindings changed; create a new operation snapshot.");
            await RequireAsync(row.ActorId, "agent.read", "agent", row.AgentId, ct);
            if (row.Kind == "environment-preview") await RequireEnvironmentPreviewAsync(row.ActorId, deployment, row.AgentId, ct);
        }
        else await RequireAsync(row.ActorId, "host.execute", "agent", row.AgentId, ct);
        if (!(await agentPolicies.RuntimeAsync(row.AgentId, ct)).ExecutionEnabled) throw new OperationException("Agent execution has been disabled.");
        if (row.Kind == "host-exec" && !(await policies.GetAsync(ct)).Policy.AllowHostActions) throw new OperationException("Host actions have been disabled.");
        if (!await db.Set<AgentEntity>().AnyAsync(x => x.Id == row.AgentId && x.Status == "approved", ct)) throw new OperationException("Agent approval has been revoked.");
        if (automationGuard is not null) await automationGuard.AuthorizeAsync(row.ActorId, row.Trigger, row.Kind, row.DeploymentId, row.AgentId, ct);
    }
    internal AgentWorkRequest Request(OperationEntity row) => JsonSerializer.Deserialize<AgentWorkRequest>(Protector(row.Id).Unprotect(row.ProtectedRequest)) ?? throw new OperationException("Invalid operation snapshot.");
    internal string ProtectResponse(Guid id, AgentWorkResponse response) => Protector(id).Protect(JsonSerializer.Serialize(response));
    internal static OperationSummary Summary(OperationEntity row) => new(row.Id, row.ActorId, row.DeploymentId, row.AgentId, row.Kind, row.State, row.Trigger, row.CreatedAt, row.CompletedAt, row.Code);
    private IDataProtector Protector(Guid id) => protection.CreateProtector("Stanza.Operation.v1", id.ToString("N"));
    private Task<OperationEntity> RequiredAsync(Guid id, CancellationToken ct) => RequiredCoreAsync(id, ct);
    private async Task<OperationEntity> RequiredCoreAsync(Guid id, CancellationToken ct) => await db.Set<OperationEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new OperationException("Operation not found.");
    private async Task<bool> CanReadAsync(Guid actor, OperationEntity row, CancellationToken ct) => row.DeploymentId is { } deployment
        ? await access.IsAllowedAsync(actor, "deployment.read", "deployment", deployment, ct)
        : await access.IsAllowedAsync(actor, "host.execute", "agent", row.AgentId, ct);
    private async Task AuthorizeAsync(Guid actor, Guid deployment, string kind, CancellationToken ct)
    {
        if (kind == "host-exec") throw new OperationException("Host actions must explicitly target an Agent without borrowing a deployment identity.");
        if (!Permissions.TryGetValue(kind, out var permission)) throw new OperationException("Unsupported operation.");
        await RequireAsync(actor, permission, "deployment", deployment, ct);
    }
    private async Task<bool> CanRevealEnvironmentPreviewAsync(Guid actor, Guid deployment, Guid agent, CancellationToken ct) =>
        await access.IsAllowedAsync(actor, "deployment.read", "deployment", deployment, ct) &&
        await access.IsAllowedAsync(actor, "agent.read", "agent", agent, ct) &&
        await access.IsAllowedAsync(actor, "environment.reveal", "instance", null, ct) &&
        await access.IsAllowedAsync(actor, "environment.reveal", "agent", agent, ct) &&
        await access.IsAllowedAsync(actor, "environment.reveal", "deployment", deployment, ct);

    private async Task RequireEnvironmentPreviewAsync(Guid actor, Guid deployment, Guid agent, CancellationToken ct)
    {
        if (!await CanRevealEnvironmentPreviewAsync(actor, deployment, agent, ct))
            throw new OperationException("Environment preview requires deployment/Agent read and reveal access to all three environment scopes.");
    }

    private async Task<bool> IsImagePolicyAsync(Guid actor, string trigger, string kind, Guid? deployment, CancellationToken ct)
    {
        if (!trigger.StartsWith("automation:", StringComparison.Ordinal)) return false;
        if (!Guid.TryParseExact(trigger[11..], "N", out var runId)) throw new OperationException("Invalid automation execution identity.");
        var run = await db.Set<AutomationRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, ct)
            ?? throw new OperationException("The automation execution no longer exists.");
        if ((run.ActionId.HasValue ? run.ActionOwnerId : run.OwnerId) != actor || run.State != "running" || run.CancelRequested || run.LeaseUntil < clock.GetUtcNow().ToUnixTimeSeconds())
            throw new OperationException("The automation execution is no longer authorized.");
        if (run.Source is not ("image-policy" or "manual-image-policy")) return false;
        if (kind != "deploy" || deployment is null || run.DeploymentId != deployment || run.Operation != "image-check" || run.ActionId is not null)
            throw new OperationException("An image policy may only redeploy its own applied deployment.");
        return true;
    }

    private static DeploymentSourcesSnapshot ApplySourceOverrides(DeploymentSourcesSnapshot sources, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0) return sources;
        if (overrides.Keys.Any(key => sources.Git.All(g => g.Destination != key))) throw new OperationException("The bound Git source was removed.");
        return sources with { Git = sources.Git.Select(g => overrides.TryGetValue(g.Destination, out var reference) ? g with { Reference = reference } : g).ToArray() };
    }

    private ActionExecutionBindings? ReadActionSelections(OperationEntity row)
    {
        using var document = JsonDocument.Parse(Protector(row.Id).Unprotect(row.ProtectedRequest));
        return document.RootElement.TryGetProperty("_stanzaActionSelections", out var selections) ? selections.Deserialize<ActionExecutionBindings>() : null;
    }

    private sealed record ImageBaseline(OperationEntity Operation, AgentWorkRequest Request, DeploymentSourcesSnapshot Sources);
    private sealed record GitDefinition(Guid Id, string Url, string Reference, string Destination, Guid? CredentialId, int Position);
    private sealed record SourceDefinitions(IReadOnlyList<GitDefinition> Git, IReadOnlyList<Guid> RegistryCredentialIds);

    private async Task<SourceDefinitions> SourceDefinitionsAsync(Guid deployment, CancellationToken ct) => new(
        await db.Set<DeploymentGitSourceEntity>().AsNoTracking().Where(x => x.DeploymentId == deployment)
            .OrderBy(x => x.Position).ThenBy(x => x.Id).Select(x => new GitDefinition(x.Id, x.Url, x.Reference, x.Destination, x.CredentialId, x.Position)).ToArrayAsync(ct),
        (await db.Set<DeploymentRegistryBindingEntity>().AsNoTracking().Where(x => x.DeploymentId == deployment).Select(x => x.CredentialId).ToArrayAsync(ct)).Order().ToArray());

    private SourceDefinitions ReadSourceDefinitions(OperationEntity row)
    {
        try
        {
            using var document = JsonDocument.Parse(Protector(row.Id).Unprotect(row.ProtectedRequest));
            return document.RootElement.TryGetProperty("_stanzaSourceDefinitions", out var definitions)
                ? definitions.Deserialize<SourceDefinitions>() ?? throw new JsonException()
                : throw new OperationException("The applied source-definition snapshot is unavailable. Create a new deployment snapshot.");
        }
        catch (Exception error) when (error is JsonException or CryptographicException)
        { throw new OperationException("The applied source-definition snapshot is invalid."); }
    }

    private async Task<ImageBaseline> ImageBaselineAsync(Guid actor, Guid deployment, Guid agent, DeploymentSourcesSnapshot fresh,
        Guid? expectedParent, Guid? currentOperation, CancellationToken ct)
    {
        var runtime = await db.Set<DeploymentRuntimeEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment, ct);
        var draft = await drafts.SnapshotForExecutionAsync(actor, deployment, ct: ct);
        var currentDraft = await db.Set<DeploymentDraftEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == deployment, ct);
        if (runtime is null || currentDraft is null || runtime.Revision < 1 || runtime.AgentId != agent || draft.AgentId != agent ||
            currentDraft.TargetAgentId != agent || draft.Revision != runtime.Revision)
            throw new OperationException("Automatic image updates cannot apply pending configuration or placement changes.");
        if (await db.Set<OperationEntity>().AnyAsync(x => x.DeploymentId == deployment && x.Id != currentOperation &&
                (x.State == "Queued" || x.State == "Running" || x.State == "CancellationRequested" || x.State == "Unknown"), ct))
            throw new OperationException("Another pending or uncertain operation prevents this automatic image update.");
        var latest = await db.Set<OperationEntity>().AsNoTracking().Where(x => x.DeploymentId == deployment && x.Kind == "deploy" && x.State == "Succeeded")
            .OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.CreatedAt).Take(2).ToArrayAsync(ct);
        if (latest.Length == 0 || latest[0].AgentId != agent || latest[0].Revision != runtime.Revision ||
            expectedParent.HasValue && latest[0].Id != expectedParent || latest[0].CompletedAt is null ||
            latest.Length > 1 && latest[0].CompletedAt == latest[1].CompletedAt && latest[0].CreatedAt == latest[1].CreatedAt)
            throw new OperationException("The applied image-policy baseline changed or cannot be identified unambiguously.");
        var applied = latest[0];
        AgentWorkRequest original;
        AgentWorkResponse response;
        try
        {
            original = Request(applied);
            var metadata = applied.ProtectedAppliedMetadata ?? applied.ProtectedResponse;
            response = metadata is null ? throw new OperationException("Applied commit metadata is unavailable.") :
                JsonSerializer.Deserialize<AgentWorkResponse>(Protector(applied.Id).Unprotect(metadata)) ?? throw new JsonException();
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        { throw new OperationException("The applied image-policy snapshot could not be decrypted."); }
        if (original.Id != applied.Id || original.DeploymentId != deployment || original.Operation != "deploy" ||
            response.Id != applied.Id || !response.Succeeded || response.Code == "uncertain" ||
            original.EnvironmentFiles.Count != 3 || !original.EnvironmentFiles.Select(x => x.Name).SequenceEqual(new[] { "global.env", "agent.env", "deployment.env" }))
            throw new OperationException("The applied image-policy snapshot is invalid.");
        var inherited = await settings.ReadInternalAsync(ct);
        var agentRow = await db.Set<AgentEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == agent, ct)
            ?? throw new OperationException("The applied Agent no longer exists.");
        string agentEnvironment;
        try { agentEnvironment = protection.CreateProtector("Stanza.AgentEnvironment.v1", agent.ToString("N")).Unprotect(agentRow.ProtectedEnvironment); }
        catch (CryptographicException) { throw new OperationException("The applied Agent environment could not be decrypted."); }
        var environments = new AgentEnvironmentFile[] { new("global.env", inherited.GlobalEnvironment), new("agent.env", agentEnvironment), new("deployment.env", draft.EnvironmentText) };
        if (!SameFiles(draft.Files, original.Files) || !SameEnvironment(environments, original.EnvironmentFiles))
            throw new OperationException("Automatic image updates cannot apply pending file or environment changes.");

        // Keep authored references/binding identities in private encrypted persistence metadata, not the
        // wire contract: an automatic request uses SHA refs, while the authored definition remains a branch.
        var definitions = ReadSourceDefinitions(applied);
        var currentDefinitions = await SourceDefinitionsAsync(deployment, ct);
        if (JsonSerializer.Serialize(definitions) != JsonSerializer.Serialize(currentDefinitions) ||
            definitions.Git.Count != fresh.Git.Count || !definitions.Git.Zip(fresh.Git).All(pair =>
                pair.First.Url == pair.Second.Url && pair.First.Reference == pair.Second.Reference && pair.First.Destination == pair.Second.Destination) ||
            !(original.Registries ?? []).Select(x => x.Server).Order(StringComparer.Ordinal)
                .SequenceEqual(fresh.Registries.Select(x => x.Server).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new OperationException("Automatic image updates cannot apply pending source definitions or registry bindings.");
        var pinned = new List<AgentGitSource>();
        foreach (var source in fresh.Git)
        {
            if (response.ResolvedSources is null || !response.ResolvedSources.TryGetValue(source.Destination, out var commit) ||
                commit is null || commit.Length != 40 || !commit.All(Uri.IsHexDigit))
                throw new OperationException("An exact applied Git commit is unavailable; automatic image update denied.");
            pinned.Add(source with { Reference = commit });
        }
        return new(applied, original, new(pinned, fresh.Registries));
    }

    private static bool SameFiles(IReadOnlyList<AgentFile> left, IReadOnlyList<AgentFile> right) => left.Count == right.Count &&
        left.Zip(right).All(pair => pair.First.Path == pair.Second.Path && pair.First.IsDirectory == pair.Second.IsDirectory &&
            pair.First.Content.AsSpan().SequenceEqual(pair.Second.Content));
    private static bool SameEnvironment(IReadOnlyList<AgentEnvironmentFile> left, IReadOnlyList<AgentEnvironmentFile> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Name == pair.Second.Name && pair.First.Text == pair.Second.Text);
    private static bool SameSources(DeploymentSourcesSnapshot current, AgentWorkRequest captured) =>
        JsonSerializer.Serialize(current.Git) == JsonSerializer.Serialize(captured.Sources ?? []) &&
        JsonSerializer.Serialize(current.Registries.OrderBy(x => x.Server, StringComparer.Ordinal)) ==
        JsonSerializer.Serialize((captured.Registries ?? []).OrderBy(x => x.Server, StringComparer.Ordinal));

    private async Task RequireAsync(Guid actor, string action, string type, Guid? id, CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(actor, action, type, id, ct)) throw new OperationException($"Permission required: {action}.");
    }
}
