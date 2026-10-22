using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Runtime;

public interface IAgentWorkExecutor
{
    Task<AgentWorkResponse> ExecuteAsync(AgentWorkRequest request, CancellationToken token = default);
}

public sealed class HostExecutionOptions
{
    public bool Enabled { get; set; }
}

/// <summary>Executes immutable, authenticated operation snapshots. Outputs are sensitive; diagnostics are codes only.</summary>
public sealed class AgentWorkExecutor(IProcessRunner runner, IOptions<AgentOptions> options,
    IOptions<DockerRuntimeOptions> dockerOptions, IOptions<HostExecutionOptions> hostOptions) : IAgentWorkExecutor
{
    private readonly SemaphoreSlim execution = new(1, 1);
    private sealed record Ledger(string Hash, AgentWorkResponse? Response);
    private sealed class WorkFailure(string code) : Exception { public string Code { get; } = code; }
    private static readonly HashSet<string> Operations = ["validate", "build", "pull", "deploy", "start", "stop", "restart", "remove", "status", "logs", "container-exec", "host-exec", "git-branches", "registry-check", "image-digests"];

    public async Task<AgentWorkResponse> ExecuteAsync(AgentWorkRequest request, CancellationToken token = default)
    {
        await execution.WaitAsync(token);
        string? secretDirectory = null;
        string? ledgerPath = null;
        string? hash = null;
        var started = false;
        var resultProduced = false;
        var resolvedSources = new Dictionary<string, string>(StringComparer.Ordinal);
        FileStream? processLease = null;
        try
        {
            if (request.Id == Guid.Empty || request.DeploymentId == Guid.Empty && request.Operation is not ("host-exec" or "git-branches" or "registry-check") || !Operations.Contains(request.Operation) ||
                request.Files is null || request.EnvironmentFiles is null ||
                request.Files.Any(x => x is null || x.Content is null || x.Path is null) ||
                request.EnvironmentFiles.Any(x => x is null || x.Text is null || x.Name is null) ||
                (request.Sources?.Count > 32) || (request.Registries?.Count > 32) ||
                (request.Sources?.Any(x => x is null || x.Url is null || x.Reference is null || x.Destination is null ||
                    x.Username?.Contains('\0') == true || x.Password?.Contains('\0') == true) ?? false) ||
                (request.Registries?.Any(x => x is null || x.Server is null || x.Username is null || x.Password is null ||
                    x.Username.Length > 4096 || x.Password.Length > 65536 || x.Username.Any(char.IsControl) || x.Password.Any(char.IsControl)) ?? false))
                return new(request.Id, false, "invalid_request");
            var policy = request.Policy ?? new AgentRuntimePolicy();
            if (!policy.ExecutionEnabled) return new(request.Id, false, "execution_disabled");
            if (policy.TimeoutSeconds is < 1 or > 3600 || policy.MaximumOutputCharacters is < 1 or > 1048576 ||
                !DockerRuntimeOptions.IsValid(new DockerRuntimeOptions { Endpoint = policy.DockerEndpoint }))
                return new(request.Id, false, "invalid_runtime_policy");
            var root = PrivateStorage.EnsureDirectory(options.Value.PersistentDataRoot);
            processLease = AcquireLease(root);
            CleanupStaleCredentials(root);
            var ledgerRoot = PrivateStorage.EnsureDirectory(Path.Combine(root, "request-ledger"));
            ledgerPath = Path.Combine(ledgerRoot, request.Id.ToString("N") + ".json");
            PrivateStorage.RejectLinks(ledgerPath);
            hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonSerializerOptions.Web)));
            if (File.Exists(ledgerPath))
            {
                if (new FileInfo(ledgerPath).Length > 8 * 1024 * 1024) return new(request.Id, false, "ledger_unavailable");
                var previous = JsonSerializer.Deserialize<Ledger>(await File.ReadAllTextAsync(ledgerPath, token));
                if (previous is null || previous.Hash != hash) return new(request.Id, false, "request_id_conflict");
                return previous.Response ?? new(request.Id, false, "uncertain");
            }
            // Fail closed rather than evict idempotency evidence and permit an old command to execute again.
            if (Directory.EnumerateFiles(ledgerRoot).Take(100001).Count() >= 100000)
                return new(request.Id, false, "ledger_capacity");
            await WriteLedgerAsync(ledgerPath, new(hash, null), false, token);
            FlushDirectory(root);
            started = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(policy.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            var workspaces = PrivateStorage.EnsureDirectory(Path.Combine(root, "operation-workspaces"));
            var directory = Path.Combine(workspaces, request.Id.ToString("N"));
            if (Directory.Exists(directory)) throw new WorkFailure("workspace_conflict");
            PrivateStorage.EnsureDirectory(directory);
            secretDirectory = PrivateStorage.EnsureDirectory(Path.Combine(root, "credentials", request.Id.ToString("N")));
            var environment = new Dictionary<string, string>
            {
                ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                ["HOME"] = secretDirectory,
                ["DOCKER_CONFIG"] = secretDirectory,
                ["DOCKER_HOST"] = policy.DockerEndpoint,
                ["LANG"] = "C.UTF-8", ["LC_ALL"] = "C.UTF-8",
                ["COMPOSE_DISABLE_ENV_FILE"] = "true",
                ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null",
                ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_LFS_SKIP_SMUDGE"] = "1"
            };
            await PrepareCredentialsAsync(request, secretDirectory, linked.Token);
            await MaterializeAsync(request, directory, linked.Token);
            var context = new WorkContext(request, policy, directory, secretDirectory, environment, linked.Token);
            await PrepareSourcesAsync(context, resolvedSources);
            var output = await RunOperationAsync(context);
            if (output?.Length > policy.MaximumOutputCharacters) throw new WorkFailure("runtime_output_limit");
            resultProduced = true;
            // Never report successful work while its temporary registry credentials remain on disk.
            Directory.Delete(secretDirectory, true);
            secretDirectory = null;
            var result = new AgentWorkResponse(request.Id, true, "succeeded", output, resolvedSources,
                request.Operation == "image-digests" && output is not null ? JsonSerializer.Deserialize<Dictionary<string, string>>(output) : null);
            await WriteLedgerAsync(ledgerPath, new(hash, result), true, CancellationToken.None);
            return result;
        }
        catch (Exception ex) when (ex is WorkFailure or ArgumentException or IOException or UnauthorizedAccessException or
            JsonException or OperationCanceledException or PlatformNotSupportedException)
        {
            var code = resultProduced ? "uncertain" : ex switch
            {
                WorkFailure failure => failure.Code,
                OperationCanceledException => "uncertain",
                ArgumentException => "invalid_snapshot",
                JsonException => "invalid_runtime_response",
                _ => "runtime_unavailable"
            };
            var result = new AgentWorkResponse(request.Id, false, code, ResolvedSources: resolvedSources);
            // A cancelled or interrupted process is ambiguous. The started ledger remains and will never rerun it.
            if (started && ledgerPath is not null && hash is not null && code != "uncertain")
            {
                try { await WriteLedgerAsync(ledgerPath, new(hash, result), true, CancellationToken.None); }
                catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                { return new(request.Id, false, "uncertain"); }
            }
            return result;
        }
        finally
        {
            if (secretDirectory is not null)
            {
                try { PrivateStorage.RejectLinks(secretDirectory); Directory.Delete(secretDirectory, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Retried under the exclusive lease before another command. */ }
            }
            processLease?.Dispose();
            execution.Release();
        }
    }

    private static FileStream AcquireLease(string root)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = Path.Combine(root, "execution.lock");
        PrivateStorage.RejectLinks(path);
        try
        {
            // OS-backed exclusive lock survives neither process exit nor a crash. It prevents a second
            // local Agent process from deleting live credentials or racing the durable request ledger.
            return new FileStream(path, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite,
                Share = FileShare.None, UnixCreateMode = PrivateStorage.FileMode });
        }
        catch (IOException) { throw new WorkFailure("agent_busy"); }
    }

    private static void CleanupStaleCredentials(string root)
    {
        var directory = PrivateStorage.EnsureDirectory(Path.Combine(root, "credentials"));
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            // Only request-owned GUID directories are ours to delete; fail closed on unexpected storage.
            if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _) || !Directory.Exists(path))
                throw new WorkFailure("credential_cleanup_failed");
            PrivateStorage.RejectLinks(path);
            Directory.Delete(path, true);
        }
    }

    private static async Task WriteLedgerAsync(string path, Ledger ledger, bool replace, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew,
                Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = PrivateStorage.FileMode }))
            {
                await JsonSerializer.SerializeAsync(stream, ledger, cancellationToken: token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            File.Move(temporary, path, replace);
            FlushDirectory(Path.GetDirectoryName(path)!);
        }
        finally { File.Delete(temporary); }
    }

    private static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Durable execution requires Linux storage.");
        var handle = Open(directory, 0x10000); // O_RDONLY | O_DIRECTORY
        if (handle < 0) throw new IOException("Durable request storage is unavailable.");
        try { if (Fsync(handle) != 0) throw new IOException("Durable request storage is unavailable."); }
        finally { Close(handle); }
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int handle);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int handle);

    private sealed record WorkContext(AgentWorkRequest Request, AgentRuntimePolicy Policy, string Directory,
        string Secrets, Dictionary<string, string> Environment, CancellationToken Token);

    public static string SnapshotPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 4096 || Path.IsPathRooted(relative) || relative.Contains('\\') ||
            relative.Any(char.IsControl) || relative.Split('/').Any(x => x is "" or "." or ".." ||
                x.Equals(".git", StringComparison.OrdinalIgnoreCase) || x.Equals(".stanza", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Invalid snapshot path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("Invalid snapshot path.");
        PrivateStorage.RejectLinks(path);
        return path;
    }

    private static async Task MaterializeAsync(AgentWorkRequest request, string directory, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (request.Files.Count > 10000 || request.EnvironmentFiles.Count > 3 ||
            request.Files.Sum(x => (long)x.Content.Length) > 24 * 1024 * 1024 ||
            request.EnvironmentFiles.Sum(x => (long)x.Text.Length) > 3 * 1024 * 1024)
            throw new WorkFailure("snapshot_limit");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Reserve every ancestor too, to reject case aliases and file-vs-directory collisions before writing.
        var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in request.Files)
        {
            SnapshotPath(directory, file.Path);
            if (file.IsDirectory && file.Content.Length != 0) throw new WorkFailure("invalid_snapshot_directory");
            if (!file.IsDirectory && request.Files.Any(other => other.Path.StartsWith(file.Path + "/", StringComparison.OrdinalIgnoreCase))) throw new WorkFailure("snapshot_collision");
            if (!paths.Add(file.Path)) throw new WorkFailure("snapshot_collision");
            for (var prefix = file.Path; prefix is not null; prefix = Path.GetDirectoryName(prefix))
            {
                if (prefix.Length == 0) break;
                if (spelling.TryGetValue(prefix, out var other) && other != prefix) throw new WorkFailure("snapshot_collision");
                spelling[prefix] = prefix;
            }
        }
        foreach (var source in request.Sources ?? [])
        {
            SnapshotPath(directory, source.Destination);
            for (var prefix = source.Destination; !string.IsNullOrEmpty(prefix); prefix = Path.GetDirectoryName(prefix))
            {
                if (spelling.TryGetValue(prefix, out var other) && other != prefix) throw new WorkFailure("snapshot_collision");
                spelling[prefix] = prefix;
            }
        }
        foreach (var file in request.Files)
        {
            var path = SnapshotPath(directory, file.Path);
            if (file.IsDirectory) { PrivateStorage.EnsureDirectory(path); continue; }
            PrivateStorage.EnsureDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, file.Content, token);
            File.SetUnixFileMode(path, PrivateStorage.FileMode);
        }
        var internalDirectory = PrivateStorage.EnsureDirectory(Path.Combine(directory, ".stanza"));
        // Explicit --env-file arguments disable implicit interpolation loading, but users may
        // legitimately reference their authored deployment .env through a service env_file.
        if (request.EnvironmentFiles.Count == 3)
        {
            var envPath = Path.Combine(directory, ".env");
            if (!File.Exists(envPath)) { await File.WriteAllTextAsync(envPath, request.EnvironmentFiles[2].Text, token); File.SetUnixFileMode(envPath, PrivateStorage.FileMode); }
        }
        for (var i = 0; i < 3; i++)
        {
            var text = i < request.EnvironmentFiles.Count ? request.EnvironmentFiles[i].Text : "";
            if (text.Contains('\0')) throw new WorkFailure("invalid_environment");
            await File.WriteAllTextAsync(Path.Combine(internalDirectory, $"environment-{i}.env"), text, new UTF8Encoding(false), token);
            File.SetUnixFileMode(Path.Combine(internalDirectory, $"environment-{i}.env"), PrivateStorage.FileMode);
        }
    }

    private static async Task PrepareCredentialsAsync(AgentWorkRequest request, string directory, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var auths = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var credential in request.Registries ?? [])
        {
            if (credential.Server.Length is < 1 or > 2048 || credential.Server.Any(char.IsControl) ||
                credential.Server.Contains('@') || credential.Username.Contains('\0') || credential.Password.Contains('\0'))
                throw new WorkFailure("invalid_registry");
            if (!auths.TryAdd(credential.Server, new { auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Username + ":" + credential.Password)) }))
                throw new WorkFailure("invalid_registry");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), JsonSerializer.Serialize(new { auths }), token);
        File.SetUnixFileMode(Path.Combine(directory, "config.json"), PrivateStorage.FileMode);
        // Static authored helper, never a credential interpolated into a shell expression or URL.
        var askpass = Path.Combine(directory, "askpass");
        await File.WriteAllTextAsync(askpass, "#!/bin/sh\ncase \"$1\" in *Username*) printf '%s' \"$STANZA_GIT_USERNAME\";; *) printf '%s' \"$STANZA_GIT_PASSWORD\";; esac\n", token);
        File.SetUnixFileMode(askpass, PrivateStorage.FileMode | UnixFileMode.UserExecute);
    }

    public static bool IsSafeGitSource(AgentGitSource source, bool allowLoopbackHttp = false) =>
        Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) &&
        source.Url.Length <= 2048 && !source.Url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) &&
        (uri.Scheme == "https" || allowLoopbackHttp && uri.Scheme == "http" && uri.IsLoopback) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        source.Reference.Length is > 0 and <= 1024 && !source.Reference.StartsWith('-') &&
        !source.Reference.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\".Contains(c)) &&
        !source.Reference.Contains("@{", StringComparison.Ordinal) && !source.Reference.Contains("..", StringComparison.Ordinal);

    private async Task PrepareSourcesAsync(WorkContext context, Dictionary<string, string> resolved)
    {
        var destinations = new List<string>();
        foreach (var source in context.Request.Sources ?? [])
        {
            if (!IsSafeGitSource(source, options.Value.AllowLoopbackHttp)) throw new WorkFailure("unsupported_git_source");
            if (context.Request.Operation == "git-branches") continue;
            var destination = SnapshotPath(context.Directory, source.Destination);
            if (File.Exists(destination) || Directory.Exists(destination) || destinations.Any(x =>
                    x.Equals(source.Destination, StringComparison.OrdinalIgnoreCase) ||
                    x.StartsWith(source.Destination + "/", StringComparison.OrdinalIgnoreCase) ||
                    source.Destination.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase)) ||
                context.Request.Files.Any(x => x.Path.StartsWith(source.Destination + "/", StringComparison.OrdinalIgnoreCase) ||
                    source.Destination.StartsWith(x.Path + "/", StringComparison.OrdinalIgnoreCase) || x.Path.Equals(source.Destination, StringComparison.OrdinalIgnoreCase)))
                throw new WorkFailure("source_collision");
            destinations.Add(source.Destination);
            PrivateStorage.EnsureDirectory(destination);
            await GitAsync(context, source, destination, ["init", "--quiet"]);
            await GitAsync(context, source, destination, ["remote", "add", "origin", source.Url]);
            await GitAsync(context, source, destination, ["fetch", "--depth=1", "--no-tags", "--", "origin", source.Reference]);
            var commit = (await GitAsync(context, source, destination, ["rev-parse", "--verify", "FETCH_HEAD^{commit}"])).Trim();
            if (commit.Length != 40 || !commit.All(Uri.IsHexDigit)) throw new WorkFailure("invalid_git_commit");
            await GitAsync(context, source, destination, ["checkout", "--quiet", "--detach", commit]);
            if (File.Exists(Path.Combine(destination, ".gitmodules"))) throw new WorkFailure("git_submodules_unsupported");
            ValidateCheckout(destination);
            resolved[source.Destination] = commit;
        }
    }

    private static void ValidateCheckout(string root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(root);
        long bytes = 0; var count = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                PrivateStorage.RejectLinks(entry);
                if (!paths.Add(Path.GetRelativePath(root, entry))) throw new WorkFailure("git_path_collision");
                if (++count > 100000) throw new WorkFailure("git_checkout_limit");
                if (Directory.Exists(entry)) pending.Push(entry);
                else if ((bytes += new FileInfo(entry).Length) > 1024L * 1024 * 1024) throw new WorkFailure("git_checkout_limit");
            }
        }
    }

    private Task<string> GitAsync(WorkContext context, AgentGitSource source, string directory, IReadOnlyList<string> arguments)
    {
        var environment = new Dictionary<string, string>(context.Environment)
        {
            ["GIT_ASKPASS"] = Path.Combine(context.Secrets, "askpass"),
            ["STANZA_GIT_USERNAME"] = source.Username ?? "",
            ["STANZA_GIT_PASSWORD"] = source.Password ?? ""
        };
        string[] prefix = ["-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "-c", "protocol.http.allow=always",
            "-c", "credential.helper=", "-c", "core.hooksPath=/dev/null", "-c", "submodule.recurse=false", "-c", "http.followRedirects=false"];
        return RunAsync(context, "/usr/bin/git", directory, [.. prefix, .. arguments], environment, "git_failed");
    }

    private async Task<string> RunAsync(WorkContext context, string executable, string directory, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null, string failure = "runtime_failed")
    {
        var result = await runner.RunAsync(new(executable, directory, arguments, environment ?? context.Environment,
            TimeSpan.FromSeconds(context.Policy.TimeoutSeconds), context.Policy.MaximumOutputCharacters), context.Token);
        if (result.TimedOut) throw new WorkFailure("uncertain");
        if (result.OutputLimitExceeded) throw new WorkFailure("runtime_output_limit");
        if (result.ExitCode != 0) throw new WorkFailure(failure);
        return result.StandardOutput;
    }

    private Task<string> DockerAsync(WorkContext context, IReadOnlyList<string> arguments) =>
        RunAsync(context, dockerOptions.Value.ExecutablePath, context.Directory, arguments);

    private List<string> ComposeArguments(WorkContext context)
    {
        // The first matched file is an explicit protocol convention, not implicit Compose discovery.
        var compose = new[] { "compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml" }
            .Where(name => context.Request.Files.Any(x => x.Path == name)).ToArray();
        if (compose.Length != 1) throw new WorkFailure("compose_file_required");
        List<string> arguments = ["compose", "--ansi", "never", "--project-directory", context.Directory,
            "--project-name", "stanza-" + context.Request.DeploymentId.ToString("N")];
        for (var i = 0; i < 3; i++) arguments.AddRange(["--env-file", Path.Combine(context.Directory, ".stanza", $"environment-{i}.env")]);
        arguments.AddRange(["--file", Path.Combine(context.Directory, compose[0])]);
        return arguments;
    }

    private async Task<string?> RunOperationAsync(WorkContext context)
    {
        var request = context.Request;
        if (request.Operation == "git-branches")
        {
            if (request.Sources is not { Count: 1 }) throw new WorkFailure("git_source_required");
            var source = request.Sources[0];
            var output = await GitAsync(context, source, context.Directory, ["ls-remote", "--heads", "--tags", "--refs", "--", source.Url]);
            var branches = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t'))
                .Where(parts => parts.Length == 2 && (parts[1].StartsWith("refs/heads/", StringComparison.Ordinal) || parts[1].StartsWith("refs/tags/", StringComparison.Ordinal)))
                .Select(parts => (parts[1].StartsWith("refs/heads/", StringComparison.Ordinal) ? parts[1][11..] : parts[1]).TrimEnd('\r')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(10000).ToArray();
            return JsonSerializer.Serialize(branches);
        }
        if (request.Operation == "registry-check")
        {
            if (request.Registries is not { Count: 1 }) throw new WorkFailure("registry_required");
            // Docker performs the registry's actual challenge/token authentication using private temporary config.
            await RegistryCheckAsync(context, request.Registries[0]);
            return null;
        }
        if (request.Operation == "host-exec") return await HostExecAsync(context);
        if (request.Operation == "container-exec") return await ContainerExecAsync(context);
        var prefix = ComposeArguments(context);
        // Compose, not an ad-hoc YAML parser, decides interpolation, services and eligible build/image operations.
        var configOutput = await DockerAsync(context, [.. prefix, "config", "--format", "json"]);
        using var config = JsonDocument.Parse(configOutput);
        if (!config.RootElement.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object)
            throw new WorkFailure("invalid_compose_configuration");
        ValidateComposePaths(context.Directory, config.RootElement, services);
        if (request.Operation == "validate") return null;
        if (request.Operation == "image-digests")
        {
            var images = new Dictionary<string, string>();
            var runtimePlatform = (await DockerAsync(context, ["version", "--format", "{{.Server.Os}}/{{.Server.Arch}}"])).Trim();
            foreach (var service in services.EnumerateObject())
            {
                if (service.Value.TryGetProperty("build", out _) || !service.Value.TryGetProperty("image", out var image)) continue;
                var platform = (service.Value.TryGetProperty("platform", out var configuredPlatform)
                    ? configuredPlatform.GetString() ?? runtimePlatform : runtimePlatform).Split('/');
                if (platform.Length is < 2 or > 3 || platform.Any(string.IsNullOrWhiteSpace)) throw new WorkFailure("invalid_runtime_platform");
                var reference = image.GetString();
                if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('-')) throw new WorkFailure("invalid_image");
                // buildx resolves the appropriate manifest list without pulling/running user workloads.
                var raw = await DockerAsync(context, ["buildx", "imagetools", "inspect", "--format", "{{json .Manifest}}", "--", reference]);
                using var manifest = JsonDocument.Parse(raw);
                var descriptor = manifest.RootElement;
                if (descriptor.TryGetProperty("manifests", out var manifests) && manifests.ValueKind == JsonValueKind.Array)
                {
                    var matches = manifests.EnumerateArray().Where(item => item.TryGetProperty("platform", out var p) &&
                        p.TryGetProperty("os", out var os) && os.GetString() == platform[0] &&
                        p.TryGetProperty("architecture", out var arch) && arch.GetString() == platform[1] &&
                        (platform.Length != 3 || p.TryGetProperty("variant", out var variant) && variant.GetString() == platform[2])).ToArray();
                    if (matches.Length != 1) throw new WorkFailure("image_platform_unavailable");
                    descriptor = matches[0];
                }
                if (!descriptor.TryGetProperty("digest", out var digest) || digest.GetString() is not { } value ||
                    !value.StartsWith("sha256:", StringComparison.Ordinal) || value.Length != 71 || !value[7..].All(Uri.IsHexDigit)) throw new WorkFailure("invalid_image_digest");
                images[service.Name] = value;
            }
            return JsonSerializer.Serialize(images);
        }
        if (request.Operation == "deploy")
        {
            if (services.EnumerateObject().Any(s => s.Value.TryGetProperty("build", out _)))
                await DockerAsync(context, [.. prefix, "build"]);
            var eligible = services.EnumerateObject().Where(s => s.Value.TryGetProperty("image", out _) && !s.Value.TryGetProperty("build", out _))
                .Select(s => s.Name).ToArray();
            if (eligible.Length > 0) await DockerAsync(context, [.. prefix, "pull", "--", .. eligible]);
            // Compose waits for running/healthy states; global operation timeout bounds the entire sequence.
            await DockerAsync(context, [.. prefix, "up", "--detach", "--no-build", "--pull", "never", "--wait", "--wait-timeout", context.Policy.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            return await DockerAsync(context, [.. prefix, "ps", "--all", "--format", "json"]);
        }
        string[] operation = request.Operation switch
        {
            "build" => ["build"], "pull" => ["pull", "--ignore-buildable"],
            "start" => ["start", "--wait", "--wait-timeout", context.Policy.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            "stop" => ["stop"], "restart" => ["restart"], "remove" => ["down"],
            "status" => ["ps", "--all", "--format", "json"],
            "logs" => ["logs", "--no-color", "--timestamps", "--tail", "200"],
            _ => throw new WorkFailure("unsupported_operation")
        };
        var result = await DockerAsync(context, [.. prefix, .. operation]);
        return request.Operation is "logs" or "status" ? result : null;
    }

    private static void ValidateComposePaths(string workspace, JsonElement configuration, JsonElement services)
    {
        static void Check(string workspace, string? value, string? relativeBase = null)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains(':')) throw new WorkFailure("compose_path_outside_workspace");
            var path = Path.GetFullPath(value, relativeBase ?? workspace);
            if (path != workspace && !path.StartsWith(workspace + "/", StringComparison.Ordinal))
                throw new WorkFailure("compose_path_outside_workspace");
            PrivateStorage.RejectLinks(path);
        }
        foreach (var service in services.EnumerateObject())
        {
            if (service.Value.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object)
            {
                var context = build.TryGetProperty("context", out var path) ? path.GetString() : workspace;
                Check(workspace, context);
                if (build.TryGetProperty("dockerfile", out var dockerfile))
                    Check(workspace, dockerfile.GetString(), Path.GetFullPath(context!, workspace));
                if (build.TryGetProperty("additional_contexts", out var additional))
                    foreach (var item in additional.EnumerateObject())
                    {
                        var value = item.Value.GetString();
                        if (value?.StartsWith("service:", StringComparison.Ordinal) == true || value?.StartsWith("docker-image:", StringComparison.Ordinal) == true) continue;
                        Check(workspace, value);
                    }
            }
            if (service.Value.TryGetProperty("env_file", out var environmentFiles))
                foreach (var file in environmentFiles.EnumerateArray())
                    Check(workspace, file.ValueKind == JsonValueKind.String ? file.GetString() : file.GetProperty("path").GetString());
        }
        foreach (var name in new[] { "secrets", "configs" })
            if (configuration.TryGetProperty(name, out var resources))
                foreach (var resource in resources.EnumerateObject())
                    if (resource.Value.TryGetProperty("file", out var file)) Check(workspace, file.GetString());
    }

    private async Task<string> ContainerExecAsync(WorkContext context)
    {
        var command = context.Request.Execution ?? throw new WorkFailure("execution_required");
        ValidateExecution(command);
        var container = command.Container;
        if (container is null)
        {
            if (!WorkspacePolicy.IsIdentifier(command.Service!)) throw new WorkFailure("service_required");
            var ids = await DockerAsync(context, [.. ComposeArguments(context), "ps", "--quiet", "--", command.Service!]);
            var candidates = ids.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (candidates.Length != 1) throw new WorkFailure("container_target_ambiguous");
            container = candidates[0];
        }
        if (container.Length is < 1 or > 256 || container.StartsWith('-') || container.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '.' and not '-'))
            throw new WorkFailure("invalid_container");
        var inspect = await DockerAsync(context, ["inspect", "--type", "container", "--format", "{{json .}}", "--", container]);
        using var document = JsonDocument.Parse(inspect);
        var root = document.RootElement;
        if (!root.TryGetProperty("Config", out var config) || !config.TryGetProperty("Labels", out var labels) ||
            !labels.TryGetProperty("com.docker.compose.project", out var project) || project.GetString() != "stanza-" + context.Request.DeploymentId.ToString("N"))
            throw new WorkFailure("container_scope_denied");
        if (!root.TryGetProperty("State", out var state) || !state.TryGetProperty("Running", out var running) || !running.GetBoolean())
            throw new WorkFailure("container_not_running");
        // Pin the inspected immutable ID, not the supplied name (which could be replaced between inspect and exec).
        var id = root.GetProperty("Id").GetString() ?? throw new WorkFailure("invalid_container");
        List<string> arguments = ["exec"];
        if (command.User is not null) arguments.AddRange(["--user", command.User]);
        if (command.WorkingDirectory is not null) arguments.AddRange(["--workdir", command.WorkingDirectory]);
        foreach (var item in command.Environment ?? new Dictionary<string, string>()) arguments.AddRange(["--env", item.Key + "=" + item.Value]);
        arguments.AddRange(["--", id, command.Executable, .. command.Arguments]);
        return await DockerAsync(context, arguments);
    }

    private async Task<string> HostExecAsync(WorkContext context)
    {
        if (!hostOptions.Value.Enabled) throw new WorkFailure("host_execution_disabled");
        var command = context.Request.Execution ?? throw new WorkFailure("execution_required");
        ValidateExecution(command);
        // Requires host PID namespace plus privileges for setns. The Agent's own mount namespace is never the target.
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/nsenter") ||
            new FileInfo("/proc/1/ns/pid").LinkTarget != new FileInfo("/proc/self/ns/pid").LinkTarget ||
            new FileInfo("/proc/1/ns/mnt").LinkTarget == new FileInfo("/proc/self/ns/mnt").LinkTarget ||
            !Directory.Exists("/proc/1/root")) throw new WorkFailure("host_namespace_prerequisites");
        if (command.User is not null) throw new WorkFailure("host_user_selection_unsupported");
        var environment = new Dictionary<string, string> { ["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin", ["LANG"] = "C.UTF-8" };
        return await RunAsync(context, "/usr/bin/nsenter", context.Directory, BuildHostArguments(command), environment, "host_execution_failed");
    }

    public static IReadOnlyList<string> BuildHostArguments(AgentExecution command)
    {
        ValidateExecution(command);
        if (command.User is not null) throw new WorkFailure("host_user_selection_unsupported");
        // Apply caller environment only AFTER entering the host, never to nsenter's loader inside the Agent.
        var targetEnvironment = new Dictionary<string, string> { ["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin", ["LANG"] = "C.UTF-8" };
        foreach (var item in command.Environment ?? new Dictionary<string, string>()) targetEnvironment[item.Key] = item.Value;
        var assignments = targetEnvironment.Select(pair => pair.Key + "=" + pair.Value).ToArray();
        return ["--target", "1", "--mount", "--uts", "--ipc", "--net", "--pid", "--root", "--wd=" + (command.WorkingDirectory ?? "/"),
                "--", "/usr/bin/env", "--ignore-environment", "--", .. assignments, command.Executable, .. command.Arguments];
    }

    private static void ValidateExecution(AgentExecution command)
    {
        if (!Path.IsPathFullyQualified(command.Executable) || command.Executable.Contains('\0') ||
            command.Arguments is null || command.Arguments.Count > 1024 || command.Arguments.Any(x => x is null || x.Contains('\0')) ||
            command.User?.Contains('\0') == true || command.WorkingDirectory?.Contains('\0') == true ||
            command.WorkingDirectory is not null && !Path.IsPathFullyQualified(command.WorkingDirectory) ||
            (command.Environment?.Any(x => x.Key.Length == 0 || x.Key.Contains('=') || x.Key.Contains('\0') || x.Value is null || x.Value.Contains('\0')) ?? false))
            throw new WorkFailure("invalid_execution");
    }

    private async Task RegistryCheckAsync(WorkContext context, AgentRegistryCredential credential)
    {
        var server = credential.Server.Contains("://", StringComparison.Ordinal) ? credential.Server : "https://" + credential.Server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new WorkFailure("invalid_registry");
        var result = await runner.RunAsync(new(dockerOptions.Value.ExecutablePath, context.Directory,
            ["login", "--username", credential.Username, "--password-stdin", "--", origin.Authority], context.Environment,
            TimeSpan.FromSeconds(context.Policy.TimeoutSeconds), context.Policy.MaximumOutputCharacters, credential.Password + "\n"), context.Token);
        if (result.TimedOut) throw new WorkFailure("registry_timeout");
        if (result.ExitCode != 0 || result.OutputLimitExceeded) throw new WorkFailure("registry_authentication_failed");
    }
}
