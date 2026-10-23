using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Portability.Participants;

public sealed record PortableAgentTemplate(string Agent, string Label, string Environment)
{
    public override string ToString() => $"Agent template {Agent} (environment redacted; no identity)";
}
public sealed record PortableEnvironment(string Global, IReadOnlyList<PortableAgentTemplate> Agents)
{
    public override string ToString() => "Portable environment (all values redacted)";
}
public sealed record PortablePublicSettings(string PublicUrl);
public sealed record PortablePolicies(OperationalPolicy Operational, AutomationLimits Automation);

public sealed class EnvironmentParticipant(StanzaDbContext db, InstanceSettingsService settings,
    AgentAdministrationService agents, ParticipantSupport support) : IPortableConfigurationParticipant
{
    public string Kind => "environment";
    public int Priority => 30;
    public async Task<IReadOnlyList<PortableResourceCandidate>> CollectReferencesAsync(PortableParticipantContext c, CancellationToken ct)
    {
        var result = new List<PortableResourceCandidate>();
        foreach (var a in await db.Set<AgentEntity>().AsNoTracking().Where(x => x.Status == "approved").ToListAsync(ct))
        {
            try { await support.Require(c.ActorId, "agent.read", "agent", a.Id, ct); }
            catch (PortabilityAccessException) { continue; }
            result.Add(new(a.Id, "agent", a.Hostname));
        }
        return result;
    }
    public async Task<JsonElement> ExportAsync(PortableParticipantContext c, CancellationToken ct)
    {
        await support.Require(c.ActorId, "environment.reveal", "instance", null, ct);
        var global = await settings.ReadAsync(c.ActorId, ct);
        var templates = new List<PortableAgentTemplate>();
        foreach (var a in await CollectReferencesAsync(c, ct))
            templates.Add(new(ParticipantSupport.Reference(c, a.Id, "agent"), a.Name, (await agents.ReadEnvironmentAsync(c.ActorId, a.Id, ct)).Text));
        return ParticipantSupport.Json(new PortableEnvironment(global.GlobalEnvironment, templates));
    }
    public async Task<IReadOnlyList<string>> ValidateAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        var section = ParticipantSupport.Read<PortableEnvironment>(content);
        ValidateAgentConsistency(c);
        await support.Require(c.ActorId, "environment.write", "instance", null, ct);
        Text(section.Global); ParticipantSupport.Bound(section.Agents.Count, 100);
        if (section.Agents.Select(x => x.Agent).Distinct().Count() != section.Agents.Count) throw new PortabilityException("Duplicate Agent environment templates.");
        foreach (var template in section.Agents)
        {
            Text(template.Environment);
            var id = await support.Resolve(c, template.Agent, "agent", ct);
            await support.Require(c.ActorId, "agent.manage", "agent", id, ct);
            await support.Require(c.ActorId, "environment.write", "agent", id, ct);
        }
        return [];
    }
    public async Task ApplyAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await ValidateAsync(content, c, ct);
        var section = ParticipantSupport.Read<PortableEnvironment>(content);
        var current = await settings.ReadAsync(c.ActorId, ct);
        await settings.SaveEditorAsync(c.ActorId, null, section.Global, current.Version, ct);
        foreach (var template in section.Agents) await ApplyAgent(template, c, agents, support, db, ct);
    }
    internal static void Text(string text)
    {
        try
        {
            if (text is null || text.Contains('\0') || new UTF8Encoding(false, true).GetByteCount(text) > 1048576)
                throw new PortabilityException("Environment files must be valid text no larger than 1 MiB.");
        }
        catch (EncoderFallbackException) { throw new PortabilityException("Invalid environment UTF-8 text."); }
    }
    internal static void ValidateAgentConsistency(PortableParticipantImportContext c)
    {
        var templates = new List<PortableAgentTemplate>();
        foreach (var s in c.Bundle?.Sections ?? [])
        {
            if (s.Kind == "environment") templates.AddRange(ParticipantSupport.Read<PortableEnvironment>(s.Content).Agents);
            if (s.Kind == "agent-templates") templates.AddRange(ParticipantSupport.Read<PortableAgentTemplate[]>(s.Content));
        }
        var mapped = templates.Where(t => c.Mapping.Agents.ContainsKey(t.Agent)).GroupBy(t => c.Mapping.Agents[t.Agent]);
        if (mapped.Any(g => g.Select(t => t.Environment).Distinct(StringComparer.Ordinal).Count() > 1))
            throw new PortabilityException("Agent templates mapped to the same destination contain conflicting environment files. Resolve the mapping instead of silently overwriting one scope.");
    }
    internal static async Task ApplyAgent(PortableAgentTemplate template, PortableParticipantImportContext c,
        AgentAdministrationService agents, ParticipantSupport support, StanzaDbContext db, CancellationToken ct)
    {
        var id = (await support.Resolve(c, template.Agent, "agent", ct))!.Value;
        var version = await db.Set<AgentEntity>().Where(x => x.Id == id).Select(x => x.Version).SingleAsync(ct);
        await agents.SaveEnvironmentAsync(c.ActorId, id, new(template.Environment, version), ct);
    }
}

public sealed class AgentTemplatesParticipant(StanzaDbContext db, AgentAdministrationService agents,
    EnvironmentParticipant environment, ParticipantSupport support) : IPortableConfigurationParticipant
{
    public string Kind => "agent-templates";
    public int Priority => 31;
    public Task<IReadOnlyList<PortableResourceCandidate>> CollectReferencesAsync(PortableParticipantContext c, CancellationToken ct) => environment.CollectReferencesAsync(c, ct);
    public async Task<JsonElement> ExportAsync(PortableParticipantContext c, CancellationToken ct)
    {
        var result = new List<PortableAgentTemplate>();
        foreach (var a in await CollectReferencesAsync(c, ct))
        {
            await support.Require(c.ActorId, "agent.manage", "agent", a.Id, ct);
            result.Add(new(ParticipantSupport.Reference(c, a.Id, "agent"), a.Name, (await agents.ReadEnvironmentAsync(c.ActorId, a.Id, ct)).Text));
        }
        return ParticipantSupport.Json(result);
    }
    public async Task<IReadOnlyList<string>> ValidateAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        var templates = ParticipantSupport.Read<PortableAgentTemplate[]>(content); ParticipantSupport.Bound(templates.Length, 100);
        EnvironmentParticipant.ValidateAgentConsistency(c);
        if (templates.Select(x => x.Agent).Distinct().Count() != templates.Length) throw new PortabilityException("Duplicate Agent templates.");
        foreach (var t in templates)
        {
            EnvironmentParticipant.Text(t.Environment);
            if (string.IsNullOrWhiteSpace(t.Label) || t.Label.Length > 253 || t.Label.Any(char.IsControl)) throw new PortabilityException("Invalid Agent template label.");
            var id = await support.Resolve(c, t.Agent, "agent", ct);
            await support.Require(c.ActorId, "agent.manage", "agent", id, ct);
            await support.Require(c.ActorId, "environment.write", "agent", id, ct);
        }
        return [];
    }
    public async Task ApplyAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await ValidateAsync(content, c, ct);
        foreach (var t in ParticipantSupport.Read<PortableAgentTemplate[]>(content))
            await EnvironmentParticipant.ApplyAgent(t, c, agents, support, db, ct);
    }
}

public sealed class PublicSettingsParticipant(InstanceSettingsService settings, ParticipantSupport support) : IPortableConfigurationParticipant
{
    public string Kind => "instance-settings";
    public int Priority => 40;
    public async Task<JsonElement> ExportAsync(PortableParticipantContext c, CancellationToken ct)
    {
        await support.Require(c.ActorId, "settings.read", "instance", null, ct);
        return ParticipantSupport.Json(new PortablePublicSettings((await settings.ReadAsync(c.ActorId, ct)).PublicUrl));
    }
    public async Task<IReadOnlyList<string>> ValidateAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await support.Require(c.ActorId, "settings.write", "instance", null, ct);
        var section = ParticipantSupport.Read<PortablePublicSettings>(content);
        settings.ValidateConfiguration(new(section.PublicUrl, "", 0));
        return [];
    }
    public async Task ApplyAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await ValidateAsync(content, c, ct);
        var original = await settings.ReadInternalAsync(ct);
        await settings.SaveEditorAsync(c.ActorId, ParticipantSupport.Read<PortablePublicSettings>(content).PublicUrl, null, original.Version, ct);
    }
}

public sealed class PoliciesParticipant(OperationalPolicyService policies, AutomationService automation, ParticipantSupport support) : IPortableConfigurationParticipant
{
    public string Kind => "operational-agent-policy";
    public bool SecurityPolicy => true;
    public int Priority => 90;
    public async Task<JsonElement> ExportAsync(PortableParticipantContext c, CancellationToken ct)
    {
        await support.SystemAdmin(c.ActorId, ct); await support.Require(c.ActorId, "settings.read", "instance", null, ct);
        return ParticipantSupport.Json(new PortablePolicies((await policies.ReadAsync(c.ActorId, ct)).Policy, await automation.LimitsAsync(ct)));
    }
    public async Task<IReadOnlyList<string>> ValidateAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await support.SystemAdmin(c.ActorId, ct); await support.Require(c.ActorId, "settings.write", "instance", null, ct);
        var section = ParticipantSupport.Read<PortablePolicies>(content);
        var policy = section.Operational;
        if (!Validator.TryValidateObject(policy, new ValidationContext(policy), [], true)
            || policy.MaximumTextBytes > policy.MaximumWorkspaceBytes || policy.MaximumUploadBytes > policy.MaximumWorkspaceBytes
            || policy.MaximumConnectionsPerUser > policy.MaximumConnections) throw new PortabilityException("Invalid operational/Agent policy limits.");
        AutomationRules.ValidateLimits(section.Automation);
        return [];
    }
    public async Task ApplyAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await ValidateAsync(content, c, ct);
        var section = ParticipantSupport.Read<PortablePolicies>(content);
        var current = await policies.GetAsync(ct);
        await policies.SaveAsync(c.ActorId, new(section.Operational, current.Version), ct);
        await automation.SaveLimitsAsync(c.ActorId, section.Automation, ct);
    }
}
