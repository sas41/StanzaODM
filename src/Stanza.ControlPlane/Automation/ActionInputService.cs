using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Automation;

public sealed record ActionChoicePage(IReadOnlyList<string> Values, string Search, int Page, bool HasMore, string? Selected = null, bool SelectedAvailable = false);
public sealed record ActionExecutionBindings(IReadOnlyDictionary<string, string> Sources, IReadOnlyDictionary<string, string> Images);

public sealed class ActionInputService(StanzaDbContext db, SourceService sources, IAgentCommandDispatcher dispatcher, IMemoryCache cache,
    Configuration.AgentPolicyService? policies = null)
{
    public async Task<IReadOnlyList<string>> ValuesAsync(Guid owner, ActionPlan plan, ActionParameter parameter, bool refresh, CancellationToken ct)
    {
        if (parameter.Input == "dropdown") return parameter.Options ?? [];
        if (parameter.Input is not ("branch" or "version") || plan.DeploymentId is not { } deployment)
            throw new AutomationException("This input has no remote choices.");
        var draft = await db.Set<DeploymentDraftEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == deployment, ct)
            ?? throw new AutomationException("The action deployment is unavailable.");
        var availableAgents = await sources.ListAgentsAsync(owner, ct);
        if (draft.TargetAgentId is not { } agent || !availableAgents.Any(x => x.Id == agent))
            throw new AutomationException("Assign an approved Agent before browsing action inputs.");
        var snapshot = await sources.PrepareAsync(owner, deployment, ct);
        var source = parameter.Input == "branch" ? snapshot.Git.SingleOrDefault(x => x.Destination == parameter.Source)
            ?? throw new AutomationException("The bound Git source is unavailable.") : null;
        // Recheck source/credential access before every cache read. Credential rotation changes the key.
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { owner, deployment, agent, parameter, snapshot })));
        if (!refresh && cache.TryGetValue<string[]>(fingerprint, out var cached)) return cached!;
        var request = new AgentWorkRequest(Guid.NewGuid(), deployment, source is null ? "registry-tags" : "git-action-branches", [], [],
            Sources: source is null ? [] : [source], Registries: source is null ? snapshot.Registries : [],
            Policy: new(), ImageRepository: parameter.Repository);
        var response = await DispatchAsync(agent, request, ct);
        if (!response.Succeeded || response.Output is null) throw new AutomationException("Remote listing failed or was denied. Check repository access and tag-listing permissions.");
        string[] values;
        try { values = JsonSerializer.Deserialize<string[]>(response.Output) ?? throw new JsonException(); }
        catch (JsonException) { throw new AutomationException("The Agent returned an invalid choice listing."); }
        if (values.Length > 10000 || values.Any(x => x is null || (source is null ? !Core.Automation.ActionInputRules.IsTag(x) : !Core.Automation.ActionInputRules.IsBranch(x))))
            throw new AutomationException("The remote choice listing is invalid or too large.");
        cache.Set(fingerprint, values, TimeSpan.FromSeconds(30));
        return values;
    }

    public async Task ValidateAsync(Guid owner, ActionPlan plan, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        foreach (var parameter in (plan.Parameters ?? []).Where(p => p.Input is "branch" or "version"))
            if (!(await ValuesAsync(owner, plan, parameter, true, ct)).Contains(values[parameter.Name], StringComparer.Ordinal))
                throw new AutomationException("The selected " + parameter.Name + " is no longer available. Refresh its choices.");
    }

    public async Task<ActionExecutionBindings> ResolveAsync(Guid owner, ActionPlan plan, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        await ValidateAsync(owner, plan, values, ct);
        var authored = AutomationRules.Bindings(plan, plan.DeploymentId, "deploy", values);
        var deployment = plan.DeploymentId!.Value;
        var draft = await db.Set<DeploymentDraftEntity>().AsNoTracking().SingleAsync(x => x.Id == deployment, ct);
        var snapshot = await sources.PrepareAsync(owner, deployment, ct);
        var selectedSources = snapshot.Git.Where(g => authored.Sources.ContainsKey(g.Destination)).Select(g => g with { Reference = authored.Sources[g.Destination] }).ToArray();
        if (selectedSources.Length != authored.Sources.Count || draft.TargetAgentId is not { } agent) throw new AutomationException("The input bindings changed. Reload the action.");
        var response = await DispatchAsync(agent, new(Guid.NewGuid(), deployment, "resolve-action-inputs", [], [], Sources: selectedSources,
            Registries: snapshot.Registries, ImageOverrides: authored.Images), ct);
        if (!response.Succeeded) throw new AutomationException("The selected branch or image could not be resolved. Refresh its choices.");
        var commits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in selectedSources)
        {
            var sha = response.ResolvedSources?.GetValueOrDefault(source.Destination);
            if (sha is null || sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new AutomationException("Invalid resolved commit returned by the Agent.");
            commits[source.Destination] = sha;
        }
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in (plan.Parameters ?? []).Where(p => p.Input == "version"))
            foreach (var service in p.Services!)
            {
                var digest = response.ResolvedImages?.GetValueOrDefault(service);
                if (digest is null || digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(Uri.IsHexDigit))
                    throw new AutomationException("Invalid resolved image digest returned by the Agent.");
                images[service] = p.Repository + "@" + digest;
            }
        return new(commits, images);
    }

    private async Task<AgentWorkResponse> DispatchAsync(Guid agent, AgentWorkRequest request, CancellationToken ct)
    {
        try
        {
            if (policies is not null) request = request with { Policy = await policies.RuntimeAsync(agent, ct) };
            var response = await dispatcher.DispatchAsync(agent, request, ct);
            if (response.Id != request.Id) throw new AutomationException("The Agent returned an invalid discovery response.");
            return response;
        }
        catch (Exception e) when (e is IOException or AgentUnavailableException || e is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new AutomationException("The Agent could not resolve remote choices. Check its connection and repository access."); }
    }
}
