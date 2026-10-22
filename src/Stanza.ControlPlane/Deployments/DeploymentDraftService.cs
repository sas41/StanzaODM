using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Deployments;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Editing;

namespace Stanza.ControlPlane.Deployments;

/// <summary>Encrypted persistent configuration drafts only. Does not validate Compose syntax or dispatch workloads.</summary>
public sealed partial class DeploymentDraftService(StanzaDbContext db, IDataProtectionProvider protection,
    AccountService accounts, AccessService access, TimeProvider clock,
    IDeploymentEditorLimitsProvider? limitsProvider = null, IDeploymentRevisionStatusProvider? revisionStatus = null,
    IDeploymentWorkspaceReservations? reservations = null)
{
    public const int MaximumTextBytes = 1024 * 1024;
    public const int MaximumUploadBytes = 4 * 1024 * 1024;
    public const int MaximumWorkspaceBytes = 16 * 1024 * 1024;
    public const int MaximumSupportingFiles = 100;
    public const string ComposePath = "compose.yaml";
    public const string EnvironmentPath = ".env";

    // Only application execution services may consume secrets without granting their operator reveal access.
    // The operation service must authorize the requested action before calling this internal API.
    internal async Task<DeploymentExecutionSnapshot> SnapshotForExecutionAsync(Guid actorId, Guid id, long? revision = null, CancellationToken ct = default)
    {
        await RequireAccountAsync(actorId, ct);
        var current = await RequiredDraftAsync(id, ct);
        var number = revision ?? current.Revision;
        var payload = await LoadAsync(id, number, ct);
        var files = new List<AgentFile> { new(ComposePath, Utf8.GetBytes(payload.ComposeText)) };
        files.AddRange(payload.Files.Select(file => new AgentFile(file.Path, file.Content.ToArray(), file.Kind == ManagedEntryKind.Directory)));
        return new(id, payload.Name, payload.TargetAgentId, number, files, payload.EnvironmentText);
    }
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public DeploymentEditorLimits EditorLimits { get; private set; } = DeploymentEditorLimits.Default;

    public async Task<DeploymentEditorLimits> GetEditorLimitsAsync(CancellationToken ct = default) =>
        EditorLimits = limitsProvider is null ? DeploymentEditorLimits.Default : await limitsProvider.GetLimitsAsync(ct);

    public async Task<IReadOnlyList<DeploymentAgentOption>> ListTargetAgentsAsync(Guid actorId, CancellationToken ct = default)
    {
        await RequireAccountAsync(actorId, ct);
        var agents = await db.Set<AgentEntity>().AsNoTracking().Where(x => x.Status == "approved").OrderBy(x => x.Hostname).ToListAsync(ct);
        var result = new List<DeploymentAgentOption>();
        foreach (var agent in agents)
            if (await access.IsAllowedAsync(actorId, "agent.read", "agent", agent.Id, ct)) result.Add(new(agent.Id, agent.Hostname));
        return result;
    }

    private async Task ValidateTargetAsync(Guid actorId, Guid? target, CancellationToken ct)
    {
        if (target is null) return;
        if (!await access.IsAllowedAsync(actorId, "agent.read", "agent", target, ct)) throw new DeploymentDraftAccessException();
        if (!await db.Set<AgentEntity>().AnyAsync(x => x.Id == target && x.Status == "approved", ct))
            throw new DeploymentDraftException("Choose a registered, approved Agent or leave this draft unassigned.");
    }

    public async Task<bool> CanCreateAsync(Guid actorId, CancellationToken ct = default) =>
        await access.IsAllowedAsync(actorId, "deployment.create", "instance", null, ct);

    public async Task<IReadOnlyList<DeploymentDraftSummary>> ListAsync(Guid actorId, CancellationToken ct = default)
    {
        await RequireAccountAsync(actorId, ct);
        var drafts = await db.Set<DeploymentDraftEntity>().AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        var result = new List<DeploymentDraftSummary>();
        foreach (var draft in drafts)
            if (await AllowedAsync(actorId, "deployment.read", draft.Id, ct)) result.Add(Summary(draft));
        return result.AsReadOnly();
    }

    public async Task<DeploymentDraftSummary> CreateAsync(Guid actorId, string name, Guid? targetAgentId,
        string composeText, string environmentText, CancellationToken ct = default)
    {
        await GetEditorLimitsAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        if (!await CanCreateAsync(actorId, ct)) throw new DeploymentDraftAccessException();
        ValidateMetadata(name, targetAgentId);
        await ValidateTargetAsync(actorId, targetAgentId, ct);
        var payload = new DraftPayload { Name = name, TargetAgentId = targetAgentId, ComposeText = composeText, EnvironmentText = environmentText };
        ValidatePayload(payload);
        var now = clock.GetUtcNow();
        var draft = new DeploymentDraftEntity { Id = Guid.NewGuid(), Name = name, TargetAgentId = targetAgentId,
            Revision = 1, CreatedAt = now, UpdatedAt = now };
        var revision = Revision(draft.Id, 1, actorId, now, payload);
        db.Set<DeploymentDraftEntity>().Add(draft);
        db.Set<DeploymentDraftRevisionEntity>().Add(revision);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            db.Entry(draft).State = EntityState.Detached;
            db.Entry(revision).State = EntityState.Detached;
        }
        return Summary(draft);
    }

    public async Task<DeploymentDraftView> ReadAsync(Guid actorId, Guid id, long? revision = null, CancellationToken ct = default)
    {
        await RequireAsync(actorId, "deployment.read", id, ct);
        var current = await RequiredDraftAsync(id, ct);
        var number = revision ?? current.Revision;
        if (number < 1 || number > current.Revision) throw new DeploymentDraftException("That saved revision is unavailable.");
        var permissions = new DeploymentDraftPermissions(await CanCreateAsync(actorId, ct),
            await AllowedAsync(actorId, "files.read", id, ct), await AllowedAsync(actorId, "files.write", id, ct),
            await AllowedAsync(actorId, "environment.reveal", id, ct), await AllowedAsync(actorId, "environment.write", id, ct));
        // Do not decrypt secret contents at all for metadata-only readers.
        var payload = permissions.CanReadFiles || permissions.CanRevealEnvironment || number != current.Revision
            ? await LoadAsync(id, number, ct) : null;
        var history = await db.Set<DeploymentDraftRevisionEntity>().AsNoTracking().Where(x => x.DeploymentId == id)
            .OrderByDescending(x => x.Number).Select(x => new DeploymentDraftRevisionSummary(x.Number, x.SavedBy, x.SavedAt)).ToListAsync(ct);
        var summary = payload is null ? Summary(current) : new(id, payload.Name, payload.TargetAgentId, current.Revision, current.UpdatedAt);
        return new(summary, number, permissions.CanReadFiles ? payload!.ComposeText : null,
            permissions.CanRevealEnvironment ? payload!.EnvironmentText : null,
            permissions.CanReadFiles ? payload!.Files.Select(file => new DeploymentDraftFileSummary(file.Path, file.Kind, file.Content.LongLength)).ToArray() : [],
            history.AsReadOnly(), permissions)
        {
            CanValidate = await AllowedAsync(actorId, "deployment.validate", id, ct),
            DeployedRevision = revisionStatus is null ? null : await revisionStatus.GetDeployedRevisionAsync(id, ct)
        };
    }

    public Task<DeploymentDraftSummary> SaveAsync(Guid actorId, Guid id, DeploymentDraftEdit edit, CancellationToken ct = default) =>
        MutateAsync(actorId, id, edit.ExpectedRevision, async payload =>
        {
            await RequireAsync(actorId, "files.write", id, ct);
            if (edit.EnvironmentText is not null) await RequireAsync(actorId, "environment.write", id, ct);
            ValidateMetadata(edit.Name, edit.TargetAgentId);
            await ValidateTargetAsync(actorId, edit.TargetAgentId, ct);
            payload.Name = edit.Name;
            payload.TargetAgentId = edit.TargetAgentId;
            if (edit.ComposeText is not null) payload.ComposeText = edit.ComposeText;
            if (edit.EnvironmentText is not null) payload.EnvironmentText = edit.EnvironmentText;
        }, ct);

    public Task<DeploymentDraftSummary> SaveTextFileAsync(Guid actorId, Guid id, long expectedRevision,
        string path, string text, bool create, CancellationToken ct = default) =>
        MutateAsync(actorId, id, expectedRevision, payload =>
        {
            ValidateText(text);
            var file = SafeFile(() => ManagedFileSnapshot.Text(path, text));
            ReplaceFile(payload, file, create);
            return Task.CompletedTask;
        }, ct);

    public Task<DeploymentDraftSummary> UploadAsync(Guid actorId, Guid id, long expectedRevision,
        string path, byte[] content, CancellationToken ct = default) =>
        MutateAsync(actorId, id, expectedRevision, payload =>
        {
            if (content is null || content.Length > EditorLimits.UploadBytes)
                throw new DeploymentDraftException("Binary upload exceeds the configured file limit.");
            ReplaceFile(payload, SafeFile(() => ManagedFileSnapshot.Binary(path, content)), create: true);
            return Task.CompletedTask;
        }, ct);

    public Task<DeploymentDraftSummary> RenameFileAsync(Guid actorId, Guid id, long expectedRevision,
        string path, string newPath, CancellationToken ct = default) =>
        MutateAsync(actorId, id, expectedRevision, payload =>
        {
            var file = RequiredFile(payload, path);
            var renamed = SafeFile(() => ToSnapshot(file).Rename(newPath));
            RequireSupportingPath(renamed.Path.Value);
            var children = payload.Files.Where(x => x.Path.StartsWith(path + "/", StringComparison.Ordinal)).ToArray();
            payload.Files.Remove(file);
            payload.Files.Add(StoredFile.From(renamed));
            foreach (var child in children)
            {
                payload.Files.Remove(child);
                payload.Files.Add(StoredFile.From(SafeFile(() => ToSnapshot(child).Rename(newPath + child.Path[path.Length..]))));
            }
            return Task.CompletedTask;
        }, ct);

    public Task<DeploymentDraftSummary> CreateDirectoryAsync(Guid actorId, Guid id, long expectedRevision,
        string path, CancellationToken ct = default) => MutateAsync(actorId, id, expectedRevision, payload =>
        {
            ReplaceFile(payload, SafeFile(() => ManagedFileSnapshot.Directory(path)), create: true);
            return Task.CompletedTask;
        }, ct);

    public Task<DeploymentDraftSummary> ImportArchiveAsync(Guid actorId, Guid id, long expectedRevision,
        string destination, byte[] bytes, CancellationToken ct = default) => MutateAsync(actorId, id, expectedRevision, payload =>
        {
            foreach (var file in ManagedArchive.ReadZip(bytes, destination, EditorLimits)) ReplaceFile(payload, file, create: true);
            return Task.CompletedTask;
        }, ct);

    public Task<DeploymentDraftSummary> ReplaceBinaryAsync(Guid actorId, Guid id, long expectedRevision,
        string path, byte[] bytes, CancellationToken ct = default) => MutateAsync(actorId, id, expectedRevision, payload =>
        {
            var existing = RequiredFile(payload, path);
            if (existing.Kind != ManagedEntryKind.Binary) throw new DeploymentDraftException("Only existing binary assets can be replaced here.");
            if (bytes.Length > EditorLimits.UploadBytes) throw new DeploymentDraftException("Binary upload exceeds the configured limit.");
            payload.Files.Remove(existing);
            payload.Files.Add(StoredFile.From(SafeFile(() => ManagedFileSnapshot.Binary(path, bytes))));
            return Task.CompletedTask;
        }, ct);

    public async Task<DeploymentRevisionDiff> DiffAsync(Guid actorId, Guid id, long revision, CancellationToken ct = default)
    {
        var before = await ReadAsync(actorId, id, revision, ct);
        var latest = await ReadAsync(actorId, id, ct: ct);
        var files = new List<DeploymentFileDiff>();
        if (before.ComposeText is not null) files.Add(new(ComposePath, "text", RevisionDiff.Compare(before.ComposeText, latest.ComposeText!)));
        if (before.EnvironmentText is not null) files.Add(new(EnvironmentPath, "text", RevisionDiff.Compare(before.EnvironmentText, latest.EnvironmentText!)));
        foreach (var path in before.Files.Select(x => x.Path).Union(latest.Files.Select(x => x.Path)).Order(StringComparer.Ordinal))
        {
            var oldEntry = before.Files.SingleOrDefault(x => x.Path == path);
            var newEntry = latest.Files.SingleOrDefault(x => x.Path == path);
            var oldFile = oldEntry is null ? null : await ReadFileAsync(actorId, id, path, revision, ct);
            var newFile = newEntry is null ? null : await ReadFileAsync(actorId, id, path, latest.ViewedRevision, ct);
            if ((oldFile is null || oldFile.Kind == ManagedEntryKind.Text) && (newFile is null || newFile.Kind == ManagedEntryKind.Text))
                files.Add(new(path, oldFile is null ? "added" : newFile is null ? "removed" : "text", RevisionDiff.Compare(oldFile?.GetText() ?? "", newFile?.GetText() ?? "")));
            else files.Add(new(path, oldFile is null ? "added" : newFile is null ? "removed" :
                oldFile.Kind == newFile.Kind && oldFile.GetBytes().AsSpan().SequenceEqual(newFile.GetBytes()) ? "unchanged" : "changed (binary/type)", []));
        }
        return new(id, revision, latest.ViewedRevision, files);
    }

    public Task<DeploymentDraftSummary> DeleteFileAsync(Guid actorId, Guid id, long expectedRevision,
        string path, CancellationToken ct = default) =>
        MutateAsync(actorId, id, expectedRevision, payload =>
        {
            payload.Files.Remove(RequiredFile(payload, path));
            payload.Files.RemoveAll(x => x.Path.StartsWith(path + "/", StringComparison.Ordinal));
            return Task.CompletedTask;
        }, ct);

    public async Task<ManagedFileSnapshot> ReadFileAsync(Guid actorId, Guid id, string path,
        long? revision = null, CancellationToken ct = default)
    {
        await RequireAsync(actorId, "deployment.read", id, ct);
        await RequireAsync(actorId, "files.read", id, ct);
        var current = await RequiredDraftAsync(id, ct);
        var payload = await LoadAsync(id, revision ?? current.Revision, ct);
        return ToSnapshot(RequiredFile(payload, path));
    }

    private async Task<DeploymentDraftSummary> MutateAsync(Guid actorId, Guid id, long expectedRevision,
        Func<DraftPayload, Task> mutation, CancellationToken ct)
    {
        await GetEditorLimitsAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Lock with security mutations so revocation cannot slip between authorization and commit.
        await SecurityWriteGuard.LockAsync(db, ct);
        await RequireAsync(actorId, "deployment.read", id, ct);
        await RequireAsync(actorId, "files.write", id, ct);
        var current = await RequiredDraftAsync(id, ct);
        if (expectedRevision != current.Revision) throw new DeploymentDraftConflictException();
        var payload = await LoadAsync(id, current.Revision, ct);
        await mutation(payload);
        ValidatePayload(payload, reservations is null ? [] : await reservations.GetSourcesAsync(id, ct));
        var next = checked(current.Revision + 1);
        var now = clock.GetUtcNow();
        // Database compare-and-swap, not a tracked in-memory version check. Revision insert and head update are atomic.
        var changed = await db.Set<DeploymentDraftEntity>().Where(x => x.Id == id && x.Revision == expectedRevision)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Revision, next)
                .SetProperty(x => x.Name, payload.Name).SetProperty(x => x.TargetAgentId, payload.TargetAgentId)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (changed != 1) throw new DeploymentDraftConflictException();
        var revision = Revision(id, next, actorId, now, payload);
        db.Set<DeploymentDraftRevisionEntity>().Add(revision);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally { db.Entry(revision).State = EntityState.Detached; }
        return new(id, payload.Name, payload.TargetAgentId, next, now);
    }

    private async Task RequireAccountAsync(Guid actorId, CancellationToken ct)
    {
        var actor = await accounts.FindAsync(actorId, ct);
        if (actor is null || !actor.Enabled) throw new DeploymentDraftAccessException();
    }

    private Task<bool> AllowedAsync(Guid actorId, string action, Guid id, CancellationToken ct) =>
        access.IsAllowedAsync(actorId, action, "deployment", id, ct);

    private async Task RequireAsync(Guid actorId, string action, Guid id, CancellationToken ct)
    {
        if (!await AllowedAsync(actorId, action, id, ct)) throw new DeploymentDraftAccessException();
    }

    private async Task<DeploymentDraftEntity> RequiredDraftAsync(Guid id, CancellationToken ct) =>
        await db.Set<DeploymentDraftEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new DeploymentDraftException("That deployment draft is unavailable.");

    private async Task<DraftPayload> LoadAsync(Guid id, long number, CancellationToken ct)
    {
        var row = await db.Set<DeploymentDraftRevisionEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == id && x.Number == number, ct)
            ?? throw new DeploymentDraftException("That saved revision is unavailable.");
        try
        {
            return JsonSerializer.Deserialize<DraftPayload>(Protector(id, number).Unprotect(row.ProtectedContent))
                ?? throw new DeploymentDraftException("The encrypted draft could not be read. Check the persisted instance encryption keys.");
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            throw new DeploymentDraftException("The encrypted draft could not be read. Check the persisted instance encryption keys.");
        }
    }

    private IDataProtector Protector(Guid id, long number) => protection.CreateProtector("Stanza.DeploymentDraft.v1",
        id.ToString("N"), number.ToString(CultureInfo.InvariantCulture));

    private DeploymentDraftRevisionEntity Revision(Guid id, long number, Guid actor, DateTimeOffset now, DraftPayload payload) =>
        new() { DeploymentId = id, Number = number, SavedBy = actor, SavedAt = now,
            ProtectedContent = Protector(id, number).Protect(JsonSerializer.SerializeToUtf8Bytes(payload)) };

    private static DeploymentDraftSummary Summary(DeploymentDraftEntity entity) =>
        new(entity.Id, entity.Name, entity.TargetAgentId, entity.Revision, entity.UpdatedAt);

    private static void ValidateMetadata(string name, Guid? targetAgentId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            throw new DeploymentDraftException("Use a draft name of 1–100 characters without control characters.");
        if (targetAgentId == Guid.Empty) throw new DeploymentDraftException("Leave Agent unassigned or enter a non-empty Agent ID.");
    }

    private void ValidateText(string text)
    {
        try
        {
            if (text is null || text.Contains('\0') || Utf8.GetByteCount(text) > EditorLimits.TextBytes)
                throw new DeploymentDraftException("Editable text must be valid UTF-8 without NUL characters and within the configured text limit.");
        }
        catch (EncoderFallbackException)
        { throw new DeploymentDraftException("Editable text must be valid UTF-8 without NUL characters and within the configured text limit."); }
    }

    private void ValidatePayload(DraftPayload payload, IReadOnlyList<GitSourceWorkspace>? sources = null)
    {
        ValidateText(payload.ComposeText);
        ValidateText(payload.EnvironmentText);
        foreach (var file in payload.Files)
        {
            RequireSupportingPath(file.Path);
            if (file.Kind == ManagedEntryKind.Text) ValidateText(ToSnapshot(file).GetText());
        }
        try
        {
            DeploymentWorkspaceValidator.Validate([ManagedFileSnapshot.Text(ComposePath, payload.ComposeText),
                ManagedFileSnapshot.Text(EnvironmentPath, payload.EnvironmentText), .. payload.Files.Select(ToSnapshot)], sources ?? [],
                new(EditorLimits.SupportingEntries + 2, EditorLimits.UploadBytes, EditorLimits.WorkspaceBytes));
        }
        catch (ArgumentException)
        { throw new DeploymentDraftException("Managed paths collide or the workspace exceeds its configured entry or byte limits."); }
    }

    private static T SafeFile<T>(Func<T> create)
    {
        try { return create(); }
        catch (ArgumentException) { throw new DeploymentDraftException("Use a safe relative file path with forward slashes; traversal, rooted paths and Windows device names are not allowed."); }
    }

    private static void RequireSupportingPath(string path)
    {
        if (path is null || path.Length > 1024)
            throw new DeploymentDraftException("Managed relative paths must be 1–1024 characters.");
        var safe = SafeFile(() => DeploymentPath.Parse(path));
        if (safe.Value.Equals(ComposePath, StringComparison.OrdinalIgnoreCase) || safe.Value.Equals(EnvironmentPath, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentDraftException("Edit the main Compose and deployment .env files in the main editor.");
    }

    private static StoredFile RequiredFile(DraftPayload payload, string path)
    {
        RequireSupportingPath(path);
        return payload.Files.SingleOrDefault(x => x.Path == path) ?? throw new DeploymentDraftException("That supporting file is unavailable.");
    }

    private static void ReplaceFile(DraftPayload payload, ManagedFileSnapshot file, bool create)
    {
        RequireSupportingPath(file.Path.Value);
        var existing = payload.Files.SingleOrDefault(x => x.Path == file.Path.Value);
        if (create && existing is not null) throw new DeploymentDraftException("A supporting file already exists at that path.");
        if (!create && (existing is null || existing.Kind != ManagedEntryKind.Text))
            throw new DeploymentDraftException("Only an existing text file can be edited here.");
        if (existing is not null) payload.Files.Remove(existing);
        payload.Files.Add(StoredFile.From(file));
    }

    private static ManagedFileSnapshot ToSnapshot(StoredFile file) => file.Kind switch
    {
        ManagedEntryKind.Text => ManagedFileSnapshot.Utf8Text(file.Path, file.Content),
        ManagedEntryKind.Binary => ManagedFileSnapshot.Binary(file.Path, file.Content),
        ManagedEntryKind.Directory => ManagedFileSnapshot.Directory(file.Path),
        _ => throw new DeploymentDraftException("This stored file type is unsupported.")
    };

    private sealed class DraftPayload
    {
        public string Name { get; set; } = "";
        public Guid? TargetAgentId { get; set; }
        public string ComposeText { get; set; } = "";
        public string EnvironmentText { get; set; } = "";
        public List<StoredFile> Files { get; set; } = [];
    }

    private sealed class StoredFile
    {
        public string Path { get; set; } = "";
        public ManagedEntryKind Kind { get; set; }
        public byte[] Content { get; set; } = [];
        public static StoredFile From(ManagedFileSnapshot file) => new() { Path = file.Path.Value, Kind = file.Kind, Content = file.GetBytes() };
    }
}
