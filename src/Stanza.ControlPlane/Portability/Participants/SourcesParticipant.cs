using System.Text.Json;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Portability.Participants;

public sealed record PortableCredential(string Id, string Name, string Kind, string Endpoint, string Username, string Password)
{
    public override string ToString() => $"Portable credential {Id} (secret redacted)";
}
public sealed record PortableGitSource(string Url, string Reference, string Destination, string? Credential);
public sealed record PortableSourceBindings(string Deployment, IReadOnlyList<PortableGitSource> Git, IReadOnlyList<string> Registries);
public sealed record PortableSources(IReadOnlyList<PortableCredential> Credentials, IReadOnlyList<PortableSourceBindings> Bindings);

public sealed class SourcesParticipant(StanzaDbContext db, SourceService sources, ParticipantSupport support) : IPortableConfigurationParticipant
{
    public string Kind => "sources";
    public int Priority => 10;
    public IReadOnlyList<PortableResourceReference> DefinedResources(JsonElement content) =>
        ParticipantSupport.Read<PortableSources>(content).Credentials.Select(c => new PortableResourceReference(c.Id, "credential", c.Name)).ToArray();

    private async Task<IReadOnlyList<CredentialSummary>> Credentials(PortableParticipantContext c, CancellationToken ct)
    {
        if (c.DeploymentIds.Count == 0) return await sources.ListCredentialsAsync(c.ActorId, ct);
        var ids = new HashSet<Guid>();
        foreach (var id in c.DeploymentIds)
        {
            var deployment = await sources.ReadAsync(c.ActorId, id, ct);
            foreach (var credential in deployment.Git.Where(g => g.CredentialId.HasValue).Select(g => g.CredentialId!.Value).Concat(deployment.RegistryCredentialIds)) ids.Add(credential);
        }
        var result = new List<CredentialSummary>();
        foreach (var id in ids.Order()) result.Add(await sources.ReadCredentialAsync(c.ActorId, id, ct));
        return result;
    }
    public async Task<IReadOnlyList<PortableResourceCandidate>> CollectReferencesAsync(PortableParticipantContext context, CancellationToken ct) =>
        (await Credentials(context, ct)).Select(c => new PortableResourceCandidate(c.Id, "credential", c.Name)).ToArray();

    public async Task<JsonElement> ExportAsync(PortableParticipantContext c, CancellationToken ct)
    {
        var credentials = new List<PortableCredential>();
        foreach (var item in await Credentials(c, ct))
        {
            // Metadata selection and secret reveal are distinct checks. Never use execution snapshots for exports.
            await sources.ReadCredentialAsync(c.ActorId, item.Id, ct);
            var secret = await sources.RevealAsync(c.ActorId, item.Id, ct);
            credentials.Add(new(ParticipantSupport.Reference(c, item.Id, "credential"), item.Name, item.Kind, item.Endpoint, secret.Username, secret.Password));
        }
        var bindings = new List<PortableSourceBindings>();
        foreach (var id in c.DeploymentIds)
        {
            var deployment = await sources.ReadAsync(c.ActorId, id, ct);
            bindings.Add(new(ParticipantSupport.Reference(c, id, "deployment"), deployment.Git.Select(g => new PortableGitSource(g.Url, g.Reference, g.Destination,
                g.CredentialId is { } credential ? ParticipantSupport.Reference(c, credential, "credential") : null)).ToArray(),
                deployment.RegistryCredentialIds.Select(x => ParticipantSupport.Reference(c, x, "credential")).ToArray()));
        }
        return ParticipantSupport.Json(new PortableSources(credentials, bindings));
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await support.Require(c.ActorId, "configuration.import", "instance", null, ct);
        var section = ParticipantSupport.Read<PortableSources>(content);
        ParticipantSupport.Bound(section.Credentials.Count); ParticipantSupport.Bound(section.Bindings.Count, 100);
        if (section.Credentials.Select(x => x.Id).Distinct().Count() != section.Credentials.Count || section.Bindings.Select(x => x.Deployment).Distinct().Count() != section.Bindings.Count)
            throw new PortabilityException("Duplicate source/credential resource definitions.");
        foreach (var credential in section.Credentials)
        {
            await support.Require(c.ActorId, "credential.write", "credential", null, ct);
            if (credential.Kind is not ("git" or "registry") || string.IsNullOrWhiteSpace(credential.Name) || credential.Name.Length > 100 || credential.Name.Any(char.IsControl)
                || string.IsNullOrEmpty(credential.Username) || credential.Username.Length > 512 || credential.Username.Any(char.IsControl)
                || string.IsNullOrEmpty(credential.Password) || credential.Password.Length > 16384 || credential.Password.Any(char.IsControl))
                throw new PortabilityException("Credential metadata or plaintext secret does not meet the credential service limits.");
            SourceValidation.Url(credential.Endpoint, credential.Kind == "registry");
        }
        foreach (var binding in section.Bindings)
        {
            if (c.Bundle?.Deployments.Any(d => d.Id == binding.Deployment) != true)
                throw new PortabilityException("Imported source bindings require a complete new deployment workspace in the bundle; they do not silently modify existing external deployments.");
            var target = await support.Resolve(c, binding.Deployment, "deployment", ct, true);
            await support.Require(c.ActorId, "sources.manage", "deployment", target, ct);
            await support.Require(c.ActorId, "files.write", "deployment", target, ct);
            ParticipantSupport.Bound(binding.Git.Count, 32); ParticipantSupport.Bound(binding.Registries.Count, 32);
            var destinations = binding.Git.Select(g => SourceValidation.Destination(g.Destination)).ToArray();
            foreach (var git in binding.Git)
            {
                var url = SourceValidation.Url(git.Url); SourceValidation.Reference(git.Reference);
                if (git.Credential is { } key)
                {
                    var credential = section.Credentials.SingleOrDefault(x => x.Id == key);
                    if (credential is not null && (credential.Kind != "git" || SourceValidation.Url(credential.Endpoint) != url))
                        throw new PortabilityException("Git credential references must match the exact repository endpoint.");
                    var credentialId = await support.Resolve(c, key, "credential", ct, true);
                    await support.Require(c.ActorId, "credential.use", "credential", credentialId, ct);
                    if (credential is null && credentialId is { } existing)
                    {
                        var metadata = await sources.ReadCredentialAsync(c.ActorId, existing, ct);
                        if (metadata.Kind != "git" || metadata.Endpoint != url) throw new PortabilityException("Mapped Git credential endpoint mismatch.");
                    }
                }
            }
            var registryEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in binding.Registries)
            {
                var credential = section.Credentials.SingleOrDefault(x => x.Id == key);
                var credentialId = await support.Resolve(c, key, "credential", ct, true);
                await support.Require(c.ActorId, "credential.use", "credential", credentialId, ct);
                var kind = credential?.Kind;
                var endpoint = credential?.Endpoint;
                if (credential is null && credentialId is { } existing)
                { var metadata = await sources.ReadCredentialAsync(c.ActorId, existing, ct); kind = metadata.Kind; endpoint = metadata.Endpoint; }
                if (kind != "registry" || !registryEndpoints.Add(SourceValidation.Url(endpoint!, true))) throw new PortabilityException("Registry bindings must use distinct registry endpoints.");
            }
            var deployment = c.Bundle?.Deployments.SingleOrDefault(d => d.Id == binding.Deployment);
            var paths = new[] { "compose.yaml", ".env" }.Concat(deployment?.Files.Select(f => f.Path) ?? []).Select(DeploymentPath.Parse).ToArray();
            if (destinations.Any(path => paths.Any(file => Collides(path, file))) || destinations.Where((path, i) => destinations.Skip(i + 1).Any(other => Collides(path, other))).Any())
                throw new PortabilityException("Git checkout paths overlap another source or managed file.");
        }
        return [];
    }

    public async Task ApplyAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        await ValidateAsync(content, c, ct);
        var section = ParticipantSupport.Read<PortableSources>(content);
        var created = new List<string>();
        try
        {
            foreach (var credential in section.Credentials)
            {
                var saved = await sources.SaveCredentialAsync(c.ActorId, null, 0,
                    new(credential.Name, credential.Kind, credential.Endpoint, credential.Username, credential.Password), ct);
                c.CreatedResources![credential.Id] = saved.Id; created.Add(credential.Id);
            }
            foreach (var binding in section.Bindings)
            {
                var deployment = (await support.Resolve(c, binding.Deployment, "deployment", ct))!.Value;
                foreach (var git in binding.Git)
                    await sources.SaveGitAsync(c.ActorId, deployment, null, 0, new(git.Url, git.Reference, git.Destination,
                        git.Credential is { } key ? await support.Resolve(c, key, "credential", ct) : null), ct);
                var registries = new List<Guid>();
                foreach (var key in binding.Registries) registries.Add((await support.Resolve(c, key, "credential", ct))!.Value);
                await sources.SetRegistriesAsync(c.ActorId, deployment, registries, [], ct);
            }
            await transaction.CommitAsync(ct);
        }
        catch { foreach (var key in created) c.CreatedResources?.Remove(key); throw; }
    }
    private static bool Collides(DeploymentPath a, DeploymentPath b) => a.IsSameOrAncestorOf(b) || b.IsSameOrAncestorOf(a);
}
