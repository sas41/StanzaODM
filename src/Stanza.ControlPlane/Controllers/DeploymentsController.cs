using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Models;
using Stanza.Core.Deployments;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Sources;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("deployments"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[DisableRequestSizeLimit]
[RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
public sealed class DeploymentsController(DeploymentDraftService drafts, IOperationService? operations = null,
    DeploymentRuntimeStatusService? runtimeStatus = null, Automation.AutomationService? automation = null, SourceService? sources = null) : Controller
{
    private Guid ActorId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try
        {
            var available = await drafts.ListAsync(ActorId, ct);
            var runtime = new Dictionary<Guid, DeploymentRuntimeStatus>();
            if (runtimeStatus is not null)
                foreach (var deployment in available)
                    if (await runtimeStatus.ReadAsync(ActorId, deployment.Id, ct) is { } state) runtime[deployment.Id] = state;
            return View(new DeploymentListModel(available, await drafts.CanCreateAsync(ActorId, ct)) { Runtime = runtime });
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
    }

    [HttpGet("create")]
    public async Task<IActionResult> Create(CancellationToken ct) =>
        await drafts.CanCreateAsync(ActorId, ct) ? View(new CreateDeploymentModel { Agents = await drafts.ListTargetAgentsAsync(ActorId, ct) }) : Forbid();

    [HttpPost("create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateDeploymentModel model, CancellationToken ct)
    {
        if (!await drafts.CanCreateAsync(ActorId, ct)) return Forbid();
        model.Agents = await drafts.ListTargetAgentsAsync(ActorId, ct);
        if (!ModelState.IsValid) return View(model);
        try
        {
            var saved = await drafts.CreateAsync(ActorId, model.Name, model.TargetAgentId, model.ComposeText ?? "", model.EnvironmentText ?? "", ct);
            TempData["Success"] = "Draft saved as revision 1. No workloads were deployed.";
            // Creation does not silently grant the creator read access.
            return RedirectToAction(nameof(Details), new { id = saved.Id });
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (Exception error) when (IsSafeFailure(error)) { ModelState.AddModelError("", SafeMessage(error)); return View(model); }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, long? revision, CancellationToken ct)
    {
        try
        {
            var view = await drafts.ReadAsync(ActorId, id, revision, ct);
            if (runtimeStatus is not null) view = view with { Runtime = await runtimeStatus.ReadAsync(ActorId, id, ct) };
            return View(view);
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (DeploymentDraftException error) { TempData["Error"] = error.Message; return RedirectToAction(nameof(Index)); }
    }

    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        try
        {
            var current = await drafts.ReadAsync(ActorId, id, ct: ct);
            if (!current.Permissions.CanWriteFiles) return Forbid();
            var model = new EditDeploymentModel { Id = id, Name = current.Draft.Name, TargetAgentId = current.Draft.TargetAgentId,
                ExpectedRevision = current.Draft.Revision, ComposeText = current.ComposeText,
                EnvironmentText = current.Permissions.CanWriteEnvironment ? current.EnvironmentText : null,
                Agents = await drafts.ListTargetAgentsAsync(ActorId, ct), CanEditCompose = current.Permissions.CanReadFiles,
                CanEditEnvironment = current.Permissions.CanRevealEnvironment && current.Permissions.CanWriteEnvironment };
            await LoadActionsAsync(model, ct);
            return View(model);
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (DeploymentDraftException error) { TempData["Error"] = error.Message; return RedirectToAction(nameof(Index)); }
    }

    [HttpPost("{id:guid}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, EditDeploymentModel model, CancellationToken ct)
    {
        model.Id = id;
        try
        {
            var current = await drafts.ReadAsync(ActorId, id, ct: ct);
            if (!current.Permissions.CanWriteFiles) return Forbid();
            model.CanEditCompose = current.Permissions.CanReadFiles;
            model.Agents = await drafts.ListTargetAgentsAsync(ActorId, ct);
            model.CanEditEnvironment = current.Permissions.CanRevealEnvironment && current.Permissions.CanWriteEnvironment;
            await LoadActionsAsync(model, ct);
            if (!ModelState.IsValid) return View(model);
            await drafts.SaveAsync(ActorId, id, new(model.Name, model.TargetAgentId, model.ExpectedRevision,
                PreserveUnchangedBrowserText(model.ComposeText, current.ComposeText),
                PreserveUnchangedBrowserText(model.EnvironmentText, current.EnvironmentText)), ct);
            TempData["Success"] = "A new immutable draft revision was saved. No workloads were changed.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (Exception error) when (IsSafeFailure(error)) { ModelState.AddModelError("", SafeMessage(error)); return View(model); }
    }

    private async Task LoadActionsAsync(EditDeploymentModel model, CancellationToken ct)
    {
        if (automation is null) return;
        if (sources is not null) ViewData["ActionSources"] = (await sources.ListDeploymentsAsync(ActorId, ct)).Where(x => x.DeploymentId == model.Id).SelectMany(x => x.Git).ToArray();
        model.CanCreateAction = await automation.CanManageActionAsync(ActorId, null, ct);
        model.Actions = (await automation.ScopedActionsAsync(ActorId, model.Id, ct)).Select(x => new ActionEditorModel
        {
            Id = x.Definition.Id, Revision = x.Definition.Revision, Name = x.Definition.Name, Enabled = x.Definition.Enabled,
            Concurrency = x.Definition.Concurrency, PlanJson = System.Text.Json.JsonSerializer.Serialize(x.Plan, new System.Text.Json.JsonSerializerOptions(Automation.AutomationRules.Json) { WriteIndented = true })
        }).ToArray();
    }

    [HttpPost("{id:guid}/actions/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAction([FromRoute] Guid id, ActionEditorModel model, CancellationToken ct)
    {
        if (automation is null) return NotFound();
        try
        {
            await drafts.ReadAsync(ActorId, id, ct: ct);
            if (!await automation.CanManageActionAsync(ActorId, model.Id, ct)) return Forbid();
            if (model.Id is { } existing && (await automation.ReadActionAsync(ActorId, existing, ct)).Plan.DeploymentId != id) return BadRequest("This action belongs to another scope.");
            if (!ModelState.IsValid) { ViewData["DeploymentId"] = id; return PartialView("_DeploymentAction", model); }
            var plan = Automation.AutomationRules.ParsePlan(model.PlanJson);
            if (plan.DeploymentId != id) return BadRequest("Actions edited here must remain in this deployment scope. Use the full action editor to change scope.");
            if (ModelState.IsValid)
            {
                var saved = await automation.SaveActionAsync(ActorId, model.Id, model.Revision, model.Name, model.Enabled, model.Concurrency, plan, ct);
                ModelState.Clear(); model.Id = saved.Id; model.Revision = saved.Revision;
                ViewData["ActionSaved"] = true;
            }
            ViewData["DeploymentId"] = id;
            if (Request.Headers["HX-Request"] == "true") return PartialView("_DeploymentAction", model);
            TempData["Success"] = ModelState.IsValid ? "Action saved. Workloads were not changed." : "Action was not saved. Review the action editor.";
            return ModelState.IsValid ? Redirect($"/deployments/{id}/edit#deployment-actions") : View("~/Views/Automation/ActionEditor.cshtml", model);
        }
        catch (Exception error) when (error is Automation.AutomationAccessException or DeploymentDraftAccessException) { return Forbid(); }
        catch (Automation.AutomationException error)
        {
            ModelState.AddModelError("", error.Message); ViewData["DeploymentId"] = id;
            return PartialView("_DeploymentAction", model);
        }
        catch (DeploymentDraftException) { return NotFound(); }
    }

    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> FileEditor(Guid id, string? path, CancellationToken ct)
    {
        try
        {
            var current = await drafts.ReadAsync(ActorId, id, ct: ct);
            if (!current.Permissions.CanReadFiles || !current.Permissions.CanWriteFiles) return Forbid();
            var file = path is null ? null : await drafts.ReadFileAsync(ActorId, id, path, ct: ct);
            if (file is not null && file.Kind != ManagedEntryKind.Text)
                throw new DeploymentDraftException("Binary assets can be downloaded, renamed, or deleted, but not edited as text.");
            return View("FileEditor", new DeploymentFileModel { Id = id, ExpectedRevision = current.Draft.Revision,
                Path = path ?? "", Text = file?.GetText() ?? "", IsNew = file is null });
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (DeploymentDraftException error) { TempData["Error"] = error.Message; return RedirectToAction(nameof(Details), new { id }); }
    }

    [HttpPost("{id:guid}/file"), ValidateAntiForgeryToken]
    public async Task<IActionResult> FileEditor(Guid id, DeploymentFileModel model, CancellationToken ct)
    {
        model.Id = id;
        try
        {
            // Do not return submitted content to an account whose read/write permission was revoked.
            var current = await drafts.ReadAsync(ActorId, id, ct: ct);
            if (!current.Permissions.CanReadFiles || !current.Permissions.CanWriteFiles) return Forbid();
            if (!ModelState.IsValid) return View(model);
            if (!model.IsNew)
            {
                var original = await drafts.ReadFileAsync(ActorId, id, model.Path, ct: ct);
                if (original.Kind == ManagedEntryKind.Text)
                    model.Text = PreserveUnchangedBrowserText(model.Text, original.GetText());
            }
            await drafts.SaveTextFileAsync(ActorId, id, model.ExpectedRevision, model.Path, model.Text ?? "", model.IsNew, ct);
            TempData["Success"] = "Supporting text file saved in a new revision.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (Exception error) when (IsSafeFailure(error)) { ModelState.AddModelError("", SafeMessage(error)); return View(model); }
    }

    [HttpPost("{id:guid}/upload"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(Guid id, long expectedRevision, string path, IFormFile? upload, CancellationToken ct, bool replace = false)
    {
        try
        {
            await drafts.GetEditorLimitsAsync(ct);
            if (!ModelState.IsValid || upload is null || Stanza.Core.Contracts.LimitPolicy.Exceeded(upload.Length, drafts.EditorLimits.UploadBytes))
                throw new DeploymentDraftException("Select a binary asset within the configured upload limit and supply its relative destination path.");
            // Bound the actual read as well as the declared length. Never trust the submitted filename as a path.
            await using var input = upload.OpenReadStream();
            using var content = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var count = await input.ReadAsync(buffer, ct);
                if (count == 0) break;
                if (Stanza.Core.Contracts.LimitPolicy.Exceeded(content.Length + count, drafts.EditorLimits.UploadBytes))
                    throw new DeploymentDraftException("Binary upload exceeds the configured limit.");
                await content.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (replace) await drafts.ReplaceBinaryAsync(ActorId, id, expectedRevision, path, content.ToArray(), ct);
            else await drafts.UploadAsync(ActorId, id, expectedRevision, path, content.ToArray(), ct);
            TempData["Success"] = "Binary asset saved in a new revision.";
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (Exception error) when (IsSafeFailure(error)) { TempData["Error"] = SafeMessage(error); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/rename-file"), ValidateAntiForgeryToken]
    public Task<IActionResult> RenameFile(Guid id, long expectedRevision, string path, string newPath, CancellationToken ct) =>
        FileMutation(id, () => drafts.RenameFileAsync(ActorId, id, expectedRevision, path, newPath, ct));

    [HttpPost("{id:guid}/directory"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateDirectory(Guid id, long expectedRevision, string path, CancellationToken ct) =>
        FileMutation(id, () => drafts.CreateDirectoryAsync(ActorId, id, expectedRevision, path, ct));

    [HttpPost("{id:guid}/archive"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportArchive(Guid id, long expectedRevision, string? destination, IFormFile? upload, CancellationToken ct)
    {
        try
        {
            await drafts.GetEditorLimitsAsync(ct);
            var current = await drafts.ReadAsync(ActorId, id, ct: ct);
            if (!current.Permissions.CanWriteFiles) return Forbid();
            if (!ModelState.IsValid || upload is null || Stanza.Core.Contracts.LimitPolicy.Exceeded(upload.Length, drafts.EditorLimits.UploadBytes))
                throw new DeploymentDraftException("Select a ZIP archive within the configured upload limit.");
            using var content = new MemoryStream();
            await using var input = upload.OpenReadStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) != 0)
            {
                if (Stanza.Core.Contracts.LimitPolicy.Exceeded(content.Length + count, drafts.EditorLimits.UploadBytes)) throw new DeploymentDraftException("Archive exceeds the configured upload limit.");
                await content.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            await drafts.ImportArchiveAsync(ActorId, id, expectedRevision, destination ?? "", content.ToArray(), ct);
            TempData["Success"] = "Archive entries saved atomically in one immutable revision.";
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (Exception error) when (IsSafeFailure(error)) { TempData["Error"] = SafeMessage(error); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet("{id:guid}/diff")]
    public async Task<IActionResult> Diff(Guid id, long revision, CancellationToken ct)
    {
        try { return View(await drafts.DiffAsync(ActorId, id, revision, ct)); }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (DeploymentDraftException error) { TempData["Error"] = error.Message; return RedirectToAction(nameof(Details), new { id }); }
    }

    [HttpPost("{id:guid}/validate"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct)
    {
        try
        {
            var current = await drafts.ReadAsync(ActorId, id, ct: ct);
            if (!current.CanValidate) return Forbid();
            if (operations is null) return StatusCode(503, "Operation queue is unavailable.");
            var operation = await operations.EnqueueAsync(new(ActorId, id, "validate"), ct);
            return Redirect($"/operations/{operation.Id}");
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (SourceAccessException) { return Forbid(); }
        catch (OperationException error) { TempData["Error"] = error.Message; return RedirectToAction(nameof(Details), new { id }); }
        catch (SourceException error) { TempData["Error"] = error.Message; return RedirectToAction(nameof(Details), new { id }); }
        catch (DeploymentDraftException error) { TempData["Error"] = error.Message; return RedirectToAction(nameof(Details), new { id }); }
    }

    [HttpPost("{id:guid}/delete-file"), ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteFile(Guid id, long expectedRevision, string path, CancellationToken ct) =>
        FileMutation(id, () => drafts.DeleteFileAsync(ActorId, id, expectedRevision, path, ct));

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, string path, long? revision, CancellationToken ct)
    {
        try
        {
            var file = await drafts.ReadFileAsync(ActorId, id, path, revision, ct);
            if (file.Kind == ManagedEntryKind.Directory) throw new DeploymentDraftException("Directories have no downloadable file bytes. Download individual files.");
            return File(file.GetBytes(), "application/octet-stream", file.Path.Value.Split('/')[^1]);
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (DeploymentDraftException error) { TempData["Error"] = error.Message; return RedirectToAction(nameof(Details), new { id }); }
    }

    [HttpGet("{id:guid}/download-definition")]
    public async Task<IActionResult> DownloadDefinition(Guid id, bool environment, long? revision, CancellationToken ct)
    {
        try
        {
            var current = await drafts.ReadAsync(ActorId, id, revision, ct);
            var text = environment ? current.EnvironmentText : current.ComposeText;
            if (text is null) return Forbid();
            return File(new UTF8Encoding(false, true).GetBytes(text), "application/octet-stream",
                environment ? DeploymentDraftService.EnvironmentPath : DeploymentDraftService.ComposePath);
        }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (DeploymentDraftException error) { TempData["Error"] = error.Message; return RedirectToAction(nameof(Details), new { id }); }
    }

    private async Task<IActionResult> FileMutation(Guid id, Func<Task<DeploymentDraftSummary>> mutation)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "Supply a valid file path and revision."; return RedirectToAction(nameof(Details), new { id }); }
        try { await mutation(); TempData["Success"] = "File change saved in a new revision."; }
        catch (DeploymentDraftAccessException) { return Forbid(); }
        catch (Exception error) when (IsSafeFailure(error)) { TempData["Error"] = SafeMessage(error); }
        return RedirectToAction(nameof(Details), new { id });
    }

    private static bool IsSafeFailure(Exception error) =>
        error is DeploymentDraftException or DeploymentDraftConflictException or DbUpdateException or DbException or CryptographicException;

    private static string SafeMessage(Exception error) => error is DeploymentDraftException or DeploymentDraftConflictException
        ? error.Message : "The encrypted draft could not be saved. Check database availability and persisted encryption keys, then reload before retrying.";

    // HTML forms normalize line endings. Preserve original bytes when the editor's content is otherwise unchanged.
    private static string? PreserveUnchangedBrowserText(string? submitted, string? original) =>
        submitted is not null && original is not null && NormalizeBrowserLineEndings(submitted) == NormalizeBrowserLineEndings(original)
            ? original : submitted;

    private static string NormalizeBrowserLineEndings(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
