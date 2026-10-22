using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Deployments;

namespace Stanza.ControlPlane.Portability;

public sealed record PortableDestinationPreview(Guid DeploymentId, Guid DestinationAgentId, string Name,
    long Revision, string Compose, IReadOnlyList<PortableFile> Files, IReadOnlyList<string> Warnings,
    EnvironmentPreview? Environment, string? EnvironmentUnavailable)
{
    public override string ToString() => $"Destination preview for {DeploymentId} (contents redacted)";
}

public sealed class PortableDestinationPreviewService(StanzaDbContext db, AccessService access,
    InstanceSettingsService settings, IDataProtectionProvider protection, DeploymentDraftService drafts,
    IPortableDraftStore store, PortabilityService portability)
{
    public async Task<PortableDestinationPreview> ReadAsync(Guid actor, Guid deploymentId, Guid destination,
        string name, CancellationToken ct = default)
    {
        await portability.ValidateAgentAsync(actor, destination, ct);
        var draft = await drafts.ReadAsync(actor, deploymentId, ct: ct);
        var content = await store.ReadAsync(actor, deploymentId, "source", "destination", ct);
        var warnings = PortableBundleValidator.Warnings(content.Compose);
        EnvironmentPreview? preview = null;
        string? unavailable = null;
        if (!await access.IsAllowedAsync(actor, "environment.reveal", "instance", null, ct)
            || !await access.IsAllowedAsync(actor, "environment.reveal", "agent", destination, ct))
            unavailable = "Destination effective values are hidden: revealing inherited Global and destination Agent environment requires separate reveal permissions. Authorized runtime validation can still use values without disclosing them.";
        else
        {
            try
            {
                var global = await settings.ReadInternalAsync(ct);
                var agent = await db.Set<AgentEntity>().AsNoTracking().SingleAsync(x => x.Id == destination, ct);
                var agentText = string.IsNullOrEmpty(agent.ProtectedEnvironment) ? "" : protection.CreateProtector("Stanza.AgentEnvironment.v1")
                    .CreateProtector(destination.ToString("N")).Unprotect(agent.ProtectedEnvironment);
                // This short-lived rule object only constructs an immutable scanner input. It is not application persistence.
                var revision = new InMemoryDeploymentRevisionService(new(2, DeploymentDraftService.MaximumTextBytes, DeploymentDraftService.MaximumWorkspaceBytes))
                    .Save(deploymentId, destination, 0, "compose.yaml", [ManagedFileSnapshot.Text("compose.yaml", content.Compose)], content.Environment);
                preview = EnvironmentLiteralPreviewScanner.Scan(EnvironmentScopeSnapshot.Capture(revision, global.GlobalEnvironment, agentText));
                if (!preview.IsSupported) unavailable = "Literal preview unavailable for this syntax. Docker Compose remains authoritative; no effective values were approximated. Destination runtime validation is still required.";
            }
            catch (CryptographicException) { unavailable = "Destination environment could not be decrypted. Check the persisted instance encryption keys before proceeding."; }
        }
        return new(deploymentId, destination, name, draft.ViewedRevision, content.Compose, content.Files, warnings, preview, unavailable);
    }
}
