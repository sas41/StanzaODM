using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Controllers;
using Stanza.ControlPlane.Models;
using Stanza.Core.Security;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Editing;
using System.IO.Compression;

namespace Stanza.Tests.Deployments;

public sealed class DeploymentsControllerTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StanzaDbContext db = null!;
    private DeploymentDraftService drafts = null!;
    private DeploymentsController controller = null!;
    private Guid actor;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(DeploymentDraftServiceTests.Options(connection));
        await db.Database.EnsureCreatedAsync();
        actor = (await new AccountService(db, TimeProvider.System).BootstrapAsync("root", "a-secure-password-123")).Id;
        drafts = DeploymentDraftServiceTests.Service(db, new EphemeralDataProtectionProvider());
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "SystemAdmin")], "test")) };
        controller = new(drafts) { ControllerContext = new() { HttpContext = http }, TempData = new TempDataDictionary(http, new MemoryTempData()) };
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public void Every_mutation_requires_post_and_antiforgery_and_all_responses_are_noncacheable()
    {
        Assert.NotNull(typeof(DeploymentsController).GetCustomAttribute<AuthorizeAttribute>());
        var cache = typeof(DeploymentsController).GetCustomAttribute<ResponseCacheAttribute>();
        Assert.NotNull(cache);
        Assert.True(cache.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cache.Location);
        var mutations = typeof(DeploymentsController).GetMethods().Where(method => method.GetCustomAttribute<HttpPostAttribute>() is not null).ToArray();
        Assert.Equal(9, mutations.Length);
        Assert.All(mutations, method => Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
        Assert.NotNull(typeof(DeploymentsController).GetCustomAttribute<RequestSizeLimitAttribute>());
        Assert.NotNull(typeof(DeploymentsController).GetCustomAttribute<RequestFormLimitsAttribute>());
    }

    [Fact]
    public void Empty_and_whitespace_text_are_not_converted_to_null_by_MVC_model_binding()
    {
        foreach (var pair in new[] { (typeof(CreateDeploymentModel), "ComposeText"), (typeof(CreateDeploymentModel), "EnvironmentText"),
                     (typeof(EditDeploymentModel), "ComposeText"), (typeof(EditDeploymentModel), "EnvironmentText"), (typeof(DeploymentFileModel), "Text") })
            Assert.False(pair.Item1.GetProperty(pair.Item2)!.GetCustomAttribute<DisplayFormatAttribute>()!.ConvertEmptyStringToNull);
    }

    [Fact]
    public async Task Create_list_detail_and_historical_views_use_persistent_drafts()
    {
        Assert.IsType<ViewResult>(await controller.Create(default));
        var result = await controller.Create(new CreateDeploymentModel { Name = "Draft", ComposeText = "services: {}\r\n", EnvironmentText = "A=value\n" }, default);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        var id = Assert.IsType<Guid>(redirect.RouteValues!["id"]);
        var list = Assert.IsType<DeploymentListModel>(Assert.IsType<ViewResult>(await controller.Index(default)).Model);
        Assert.Equal(id, Assert.Single(list.Drafts).Id);
        var detail = Assert.IsType<DeploymentDraftView>(Assert.IsType<ViewResult>(await controller.Details(id, 1, default)).Model);
        Assert.Equal("A=value\n", detail.EnvironmentText);
        var download = Assert.IsType<FileContentResult>(await controller.DownloadDefinition(id, false, 1, default));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("services: {}\r\n"), download.FileContents);
    }

    [Fact]
    public async Task Main_editor_preserves_unchanged_mixed_line_endings_and_can_explicitly_clear_files()
    {
        const string compose = "# comment\r\nservices: {}\n";
        const string environment = "A=first\nB=second\r\n";
        var draft = await drafts.CreateAsync(actor, "Original", null, compose, environment);
        var get = Assert.IsType<EditDeploymentModel>(Assert.IsType<ViewResult>(await controller.Edit(draft.Id, default)).Model);
        Assert.True(get.CanEditCompose);
        Assert.True(get.CanEditEnvironment);
        var save = await controller.Edit(draft.Id, new EditDeploymentModel { Name = "Renamed", ExpectedRevision = 1,
            ComposeText = compose.ReplaceLineEndings("\r\n"), EnvironmentText = environment.ReplaceLineEndings("\r\n") }, default);
        Assert.IsType<RedirectToActionResult>(save);
        var saved = await drafts.ReadAsync(actor, draft.Id);
        Assert.Equal(compose, saved.ComposeText);
        Assert.Equal(environment, saved.EnvironmentText);
        await controller.Edit(draft.Id, new EditDeploymentModel { Name = "Empty", ExpectedRevision = 2, ComposeText = "", EnvironmentText = "" }, default);
        var cleared = await drafts.ReadAsync(actor, draft.Id);
        Assert.Equal("", cleared.ComposeText);
        Assert.Equal("", cleared.EnvironmentText);
    }

    [Fact]
    public async Task Stale_editor_preserves_submitted_text_and_reports_a_safe_conflict()
    {
        var draft = await drafts.CreateAsync(actor, "Original", null, "", "");
        await drafts.SaveAsync(actor, draft.Id, new("Changed", null, 1, "new", null));
        var model = new EditDeploymentModel { Name = "Stale", ExpectedRevision = 1, ComposeText = "unsaved user text" };
        var result = Assert.IsType<ViewResult>(await controller.Edit(draft.Id, model, default));
        Assert.Same(model, result.Model);
        Assert.Equal("unsaved user text", model.ComposeText);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal("new", (await drafts.ReadAsync(actor, draft.Id)).ComposeText);
    }

    [Fact]
    public async Task Unicode_line_separator_changes_are_real_edits_not_browser_normalization()
    {
        var draft = await drafts.CreateAsync(actor, "Separators", null, "# first\u2028second", "");
        await controller.Edit(draft.Id, new EditDeploymentModel { Name = "Separators", ExpectedRevision = 1,
            ComposeText = "# first\nsecond" }, default);
        Assert.Equal("# first\nsecond", (await drafts.ReadAsync(actor, draft.Id)).ComposeText);
    }

    [Fact]
    public async Task Supporting_text_editor_upload_download_rename_and_delete_work_without_host_paths()
    {
        var draft = await drafts.CreateAsync(actor, "Files", null, "", "");
        var create = Assert.IsType<DeploymentFileModel>(Assert.IsType<ViewResult>(await controller.FileEditor(draft.Id, (string?)null, default)).Model);
        Assert.True(create.IsNew);
        await controller.FileEditor(draft.Id, new DeploymentFileModel { Path = "Dockerfile", ExpectedRevision = 1, IsNew = true, Text = "FROM scratch\n" }, default);
        var edit = Assert.IsType<DeploymentFileModel>(Assert.IsType<ViewResult>(await controller.FileEditor(draft.Id, "Dockerfile", default)).Model);
        Assert.Equal("FROM scratch\n", edit.Text);
        await controller.FileEditor(draft.Id, new DeploymentFileModel { Path = "Dockerfile", ExpectedRevision = 2, IsNew = false, Text = "FROM scratch\r\n" }, default);
        Assert.Equal("FROM scratch\n", (await drafts.ReadFileAsync(actor, draft.Id, "Dockerfile")).GetText());
        using var stream = new MemoryStream([0, 255, 10]);
        var upload = new FormFile(stream, 0, stream.Length, "upload", "../../untrusted-name");
        await controller.Upload(draft.Id, 3, "asset.bin", upload, default);
        var download = Assert.IsType<FileContentResult>(await controller.Download(draft.Id, "asset.bin", null, default));
        Assert.Equal(new byte[] { 0, 255, 10 }, download.FileContents);
        Assert.Equal("asset.bin", download.FileDownloadName);
        await controller.RenameFile(draft.Id, 4, "asset.bin", "assets/new.bin", default);
        await controller.DeleteFile(draft.Id, 5, "assets/new.bin", default);
        Assert.Single((await drafts.ReadAsync(actor, draft.Id)).Files);
    }

    [Fact]
    public async Task Ordinary_reader_cannot_reveal_environment_or_bypass_write_checks()
    {
        var accounts = new AccountService(db, TimeProvider.System);
        var user = await accounts.CreateAsync(actor, "reader", "a-secure-password-123", AccountRole.User);
        var draft = await drafts.CreateAsync(actor, "Private", null, "compose secret", "environment secret");
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test"));
        Assert.IsType<ForbidResult>(await controller.Create(default));
        Assert.IsType<ForbidResult>(await controller.Details(draft.Id, null, default));
        var access = new AccessService(db, TimeProvider.System);
        await access.AddGrantAsync(actor, user.Id, null, "deployment.read", "deployment", draft.Id);
        await access.AddGrantAsync(actor, user.Id, null, "files.read", "deployment", draft.Id);
        Assert.IsType<ForbidResult>(await controller.DownloadDefinition(draft.Id, true, null, default));
        Assert.IsType<FileContentResult>(await controller.DownloadDefinition(draft.Id, false, null, default));
        Assert.IsType<ForbidResult>(await controller.Edit(draft.Id, default));
        Assert.IsType<ForbidResult>(await controller.Edit(draft.Id, new EditDeploymentModel { Name = "Bypass", ExpectedRevision = 1, ComposeText = "replace" }, default));
        Assert.IsType<ForbidResult>(await controller.FileEditor(draft.Id, new DeploymentFileModel { Path = "test", IsNew = true, ExpectedRevision = 1 }, default));
        Assert.IsType<ForbidResult>(await controller.DeleteFile(draft.Id, 1, "test", default));
        Assert.Equal(1, (await drafts.ReadAsync(actor, draft.Id)).ViewedRevision);
    }

    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [Fact]
    public async Task Validation_enqueues_only_authorized_durable_operation_and_redirects_to_its_result()
    {
        var queue = new RecordingQueue();
        var queuedController = new DeploymentsController(drafts, queue)
        { ControllerContext = controller.ControllerContext, TempData = controller.TempData };
        var draft = await drafts.CreateAsync(actor, "Validate", null, "invalid yaml [", "secret=hidden");
        var result = Assert.IsType<RedirectResult>(await queuedController.Validate(draft.Id, default));
        Assert.Equal($"/operations/{queue.Id}", result.Url);
        Assert.Equal(new OperationSubmission(actor, draft.Id, "validate"), Assert.Single(queue.Submissions));
        Assert.Equal(1, (await drafts.ReadAsync(actor, draft.Id)).Draft.Revision);
        var user = await new AccountService(db, TimeProvider.System).CreateAsync(actor, "validator", "a-secure-password-123", AccountRole.User);
        var access = new AccessService(db, TimeProvider.System);
        await access.AddGrantAsync(actor, user.Id, null, "deployment.read", "deployment", draft.Id);
        queuedController.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test"));
        Assert.IsType<ForbidResult>(await queuedController.Validate(draft.Id, default));
        Assert.Single(queue.Submissions);
        await access.AddGrantAsync(actor, user.Id, null, "deployment.validate", "deployment", draft.Id);
        Assert.IsType<RedirectResult>(await queuedController.Validate(draft.Id, default));
        Assert.Equal(2, queue.Submissions.Count);
        // A validator does not need permission to reveal the environment in order to use the queue.
        Assert.Null((await drafts.ReadAsync(user.Id, draft.Id)).EnvironmentText);
    }

    [Fact]
    public async Task Directory_archive_and_binary_replacement_endpoints_preserve_exact_bytes_and_revisions()
    {
        var draft = await drafts.CreateAsync(actor, "Files", null, "", "");
        await controller.CreateDirectory(draft.Id, 1, "assets", default);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        { using var file = zip.CreateEntry("binary").Open(); file.Write([0, 255, 13, 10]); }
        output.Position = 0;
        await controller.ImportArchive(draft.Id, 2, "assets", new FormFile(output, 0, output.Length, "upload", "ignored.zip"), default);
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, Assert.IsType<FileContentResult>(await controller.Download(draft.Id, "assets/binary", null, default)).FileContents);
        using var replacement = new MemoryStream([255, 0]);
        await controller.Upload(draft.Id, 3, "assets/binary", new FormFile(replacement, 0, replacement.Length, "upload", "ignored"), default, true);
        Assert.Equal(new byte[] { 255, 0 }, Assert.IsType<FileContentResult>(await controller.Download(draft.Id, "assets/binary", null, default)).FileContents);
        Assert.IsType<RedirectToActionResult>(await controller.Download(draft.Id, "assets", null, default));
        var diff = Assert.IsType<DeploymentRevisionDiff>(Assert.IsType<ViewResult>(await controller.Diff(draft.Id, 3, default)).Model);
        Assert.Contains(diff.Files, x => x.Path == "assets/binary" && x.Status == "changed (binary/type)");
    }

    private sealed class RecordingQueue : IOperationService
    {
        public Guid Id { get; } = Guid.NewGuid();
        public List<OperationSubmission> Submissions { get; } = [];
        public Task<OperationSummary> EnqueueAsync(OperationSubmission submission, CancellationToken token = default)
        {
            Submissions.Add(submission);
            return Task.FromResult(new OperationSummary(Id, submission.ActorId, submission.DeploymentId, Guid.NewGuid(), submission.Kind, "queued", "manual", 0, null, null));
        }
        public Task<OperationSummary> WaitAsync(Guid actorId, Guid operationId, CancellationToken token = default) => throw new NotSupportedException();
        public Task CancelAsync(Guid actorId, Guid operationId, CancellationToken token = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Validation_queue_errors_are_actionable_instead_of_a_server_error()
    {
        var draft = await drafts.CreateAsync(actor, "Unassigned", null, "", "");
        var queuedController = new DeploymentsController(drafts, new RejectedQueue())
        { ControllerContext = controller.ControllerContext, TempData = controller.TempData };
        Assert.IsType<RedirectToActionResult>(await queuedController.Validate(draft.Id, default));
        Assert.Equal("Assign an approved Agent before running this operation.", queuedController.TempData["Error"]);
    }

    private sealed class RejectedQueue : IOperationService
    {
        public Task<OperationSummary> EnqueueAsync(OperationSubmission submission, CancellationToken token = default) =>
            throw new OperationException("Assign an approved Agent before running this operation.");
        public Task<OperationSummary> WaitAsync(Guid actorId, Guid operationId, CancellationToken token = default) => throw new NotSupportedException();
        public Task CancelAsync(Guid actorId, Guid operationId, CancellationToken token = default) => throw new NotSupportedException();
    }
}
