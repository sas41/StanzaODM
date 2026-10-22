using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Portability;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("portability"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[TypeFilter(typeof(PortabilityExceptionFilter))]
[RequestSizeLimit(34 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 33 * 1024 * 1024, ValueLengthLimit = 128 * 1024)]
public sealed class PortabilityController(PortabilityService portability, PortableMoveService moves,
    DeploymentDraftService drafts, AccessService access, StanzaDbContext db,
    PortableDestinationPreviewService destinationPreview) : Controller
{
    private Guid Actor => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try
        {
            var agents = new List<PortabilityAgentModel>();
            foreach (var a in await db.Set<AgentEntity>().AsNoTracking().Where(x => x.Status == "approved").ToListAsync(ct))
                if (await access.IsAllowedAsync(Actor, "agent.read", "agent", a.Id, ct)
                    && await access.IsAllowedAsync(Actor, "agent.manage", "agent", a.Id, ct)) agents.Add(new(a.Id, a.Hostname));
            return View(new PortabilityIndexModel(await drafts.ListAsync(Actor, ct), agents, portability.SupportedSections));
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
    }

    [HttpPost("export"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Export(Guid[] deploymentIds, string[] sections, bool acknowledgePlaintext, CancellationToken ct)
    {
        try
        {
            var bytes = await portability.ExportAsync(Actor, new(deploymentIds, sections, acknowledgePlaintext), ct);
            return File(bytes, "application/json", "stanza-plaintext-configuration-v1.json");
        }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
    }

    [HttpPost("preview"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(IFormFile? bundle, string? agentMappings, string? deploymentNames, string? resourceMappings, CancellationToken ct)
    {
        try
        {
            if (bundle is null || bundle.Length > PortableBundleValidator.MaximumBundleBytes) throw new PortabilityException("Select a JSON bundle no larger than 32 MiB.");
            await using var stream = bundle.OpenReadStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var count = await stream.ReadAsync(chunk, ct);
                if (count == 0) break;
                if (buffer.Length + count > PortableBundleValidator.MaximumBundleBytes) throw new PortabilityException("The bundle exceeds 32 MiB.");
                await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
            }
            var mapping = new PortableImportMapping(ParseMap<Guid>(agentMappings), ParseMap<string>(deploymentNames), ParseMap<string>(resourceMappings));
            var preview = await portability.PreviewAsync(Actor, buffer.ToArray(), mapping, ct);
            return View("Preview", preview);
        }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
    }

    [HttpGet("preview/{id:guid}")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
    {
        try { return View(await portability.ReadPreviewAsync(Actor, id, ct)); }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
    }

    [HttpPost("apply/{id:guid}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(Guid id, string fingerprint, bool acknowledgeApply, CancellationToken ct)
    {
        try { return View("Result", await portability.ApplyAsync(Actor, id, fingerprint, acknowledgeApply, ct)); }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
    }

    [HttpGet("result/{id:guid}")]
    public async Task<IActionResult> Result(Guid id, CancellationToken ct)
    {
        try { return View(await portability.ReadResultAsync(Actor, id, ct)); }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
    }

    [HttpPost("clone"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Clone(Guid sourceId, string name, Guid destinationAgent, bool acknowledgeStorage, string[] sections, CancellationToken ct)
    {
        try
        {
            var id = await portability.CloneAsync(Actor, sourceId, name, destinationAgent, acknowledgeStorage, sections, ct);
            TempData["Success"] = $"Clone {id} created as configuration only. No workloads started; automation disabled; access grants were not copied.";
            return RedirectToAction(nameof(Index));
        }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
        catch (PortablePartialCreationException error) { return Error($"Clone {error.DeploymentId} is partially configured and inactive; inspect it before proceeding."); }
        catch (DeploymentDraftException error) { return Error(error.Message); }
    }

    [HttpPost("destination-preview"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DestinationPreview(Guid sourceId, Guid destinationAgent, string? name, CancellationToken ct)
    {
        try { return View(await destinationPreview.ReadAsync(Actor, sourceId, destinationAgent, name ?? "New clone", ct)); }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
        catch (DeploymentDraftException error) { return Error(error.Message); }
    }

    [HttpPost("move"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Move(Guid sourceId, Guid destinationAgent, bool acknowledgeStorage, CancellationToken ct)
    {
        try
        {
            var id = await moves.QueueAsync(Actor, sourceId, destinationAgent, acknowledgeStorage, ct);
            return RedirectToAction(nameof(Move), new { id });
        }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
    }

    [HttpGet("move/{id:guid}")]
    public async Task<IActionResult> Move(Guid id, CancellationToken ct)
    {
        try
        {
            var move = await moves.ReadAsync(Actor, id, ct);
            ViewData["CanCleanup"] = move.State == "complete" && move.ActorId == Actor
                && await access.IsAllowedAsync(Actor, "deployment.remove", "deployment", move.DeploymentId, ct);
            return View(move);
        }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
    }

    [HttpPost("move/{id:guid}/cleanup"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cleanup(Guid id, bool acknowledgeCleanup, CancellationToken ct)
    {
        try { await moves.QueueSourceCleanupAsync(Actor, id, acknowledgeCleanup, ct); return RedirectToAction(nameof(Move), new { id }); }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
    }

    [HttpPost("move/{id:guid}/inspect"), ValidateAntiForgeryToken]
    public async Task<IActionResult> InspectRecovery(Guid id, bool destination, CancellationToken ct)
    {
        try
        {
            var operation = await moves.QueueRecoveryInspectionAsync(Actor, id, destination, ct);
            TempData["Success"] = $"Read-only runtime status operation {operation} queued. Inspect its result in operation history before closing recovery.";
            return RedirectToAction(nameof(Move), new { id });
        }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
    }

    [HttpPost("move/{id:guid}/close-recovery"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseRecovery(Guid id, bool acknowledgeRecovery, CancellationToken ct)
    {
        try { await moves.CloseRecoveryAsync(Actor, id, acknowledgeRecovery, ct); return RedirectToAction(nameof(Move), new { id }); }
        catch (PortabilityAccessException) { return Forbid(); }
        catch (PortabilityException error) { return Error(error.Message); }
    }

    private IActionResult Error(string message) { TempData["Error"] = message; return RedirectToAction(nameof(Index)); }
    private static IReadOnlyDictionary<string, T> ParseMap<T>(string? input)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, T>>(string.IsNullOrWhiteSpace(input) ? "{}" : input,
                PortableBundleValidator.JsonOptions) ?? throw new PortabilityException("Mappings must be JSON objects.");
            if (values.Count > 500 || values.Any(x => x.Key.Length > 100 || x.Value is null)) throw new PortabilityException("Mappings exceed reference limits or contain null values.");
            return values;
        }
        catch (JsonException) { throw new PortabilityException("Mappings must be JSON objects from logical names to destination IDs or names."); }
    }
}
