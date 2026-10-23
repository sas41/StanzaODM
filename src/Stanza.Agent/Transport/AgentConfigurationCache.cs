using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Transport;

/// <summary>Private authored configuration cache. Never modifies runtime workspaces or invokes Docker.</summary>
public sealed class AgentConfigurationCache(IOptions<AgentOptions> options)
{
    private AgentConfiguration? pending;
    public bool ExecutionEnabled { get; private set; }
    public string Root => Path.Combine(options.Value.PersistentDataRoot, "configuration-cache");

    public void Begin(AgentConfiguration configuration)
    {
        if (pending is not null || configuration.AgentId == Guid.Empty || configuration.DeploymentIds is null ||
            configuration.DeploymentIds.Count > 10000 || configuration.DeploymentIds.Contains(Guid.Empty) ||
            configuration.DeploymentIds.Distinct().Count() != configuration.DeploymentIds.Count || configuration.Policy is null ||
            !DockerRuntimeOptions.IsValid(new() { Endpoint = configuration.Policy.DockerEndpoint,
                OperationTimeoutSeconds = configuration.Policy.TimeoutSeconds, MaxOutputCharacters = configuration.Policy.MaximumOutputCharacters }))
            throw new IOException("Invalid Agent configuration.");
        ValidateEnvironment(configuration.GlobalEnvironment);
        ValidateEnvironment(configuration.AgentEnvironment);
        PrivateStorage.EnsureDirectory(Root);
        PrivateStorage.EnsureDirectory(Path.Combine(Root, "deployments"));
        pending = configuration;
    }

    public async Task ApplyAsync(AgentDeploymentConfiguration deployment, CancellationToken ct)
    {
        if (pending is null || !pending.DeploymentIds.Contains(deployment.Id) || deployment.Revision < 1 ||
            string.IsNullOrWhiteSpace(deployment.Name) || deployment.Name.Length > 100 || deployment.Files is null ||
            deployment.Files.Count > 10000 || deployment.Files.Any(x => x is null || x.Content is null || x.Path is null) ||
            deployment.Files.Sum(x => (long)x.Content.Length) > 24 * 1024 * 1024 || deployment.Sources is null ||
            deployment.Sources.Count > 32 || deployment.RegistryCredentialIds is null || deployment.RegistryCredentialIds.Count > 32)
            throw new IOException("Invalid deployment configuration.");
        ValidateEnvironment(deployment.Environment);
        var staging = Path.Combine(Root, "incoming");
        var previous = Path.Combine(Root, "previous");
        var target = Path.Combine(Root, "deployments", deployment.Id.ToString("N"));
        // Only these cache-owned paths may be removed. Reject links before recursive cleanup.
        RemoveDirectory(staging);
        RemoveDirectory(previous);
        PrivateStorage.EnsureDirectory(staging);
        try
        {
            var files = PrivateStorage.EnsureDirectory(Path.Combine(staging, "files"));
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in deployment.Files)
            {
                var path = AgentWorkExecutor.SnapshotPath(files, file.Path);
                if (!paths.Add(file.Path) || file.Path.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
                    file.IsDirectory && file.Content.Length != 0)
                    throw new IOException("Invalid synchronized file.");
                for (var prefix = file.Path; !string.IsNullOrEmpty(prefix); prefix = Path.GetDirectoryName(prefix))
                {
                    if (spelling.TryGetValue(prefix, out var prior) && prefix != prior)
                        throw new IOException("Case-conflicting synchronized path.");
                    spelling[prefix] = prefix;
                }
                if (file.IsDirectory) PrivateStorage.EnsureDirectory(path);
                else
                {
                    PrivateStorage.EnsureDirectory(Path.GetDirectoryName(path)!);
                    await WriteAsync(path, file.Content, ct);
                }
            }
            if (!paths.Contains("compose.yaml")) throw new IOException("Missing synchronized Compose file.");
            foreach (var source in deployment.Sources)
                if (source is null || string.IsNullOrWhiteSpace(source.Url) || string.IsNullOrWhiteSpace(source.Reference))
                    throw new IOException("Invalid synchronized source.");
                else AgentWorkExecutor.SnapshotPath(files, source.Destination);
            await WriteAsync(Path.Combine(files, ".env"), Encoding.UTF8.GetBytes(deployment.Environment), ct);
            await WriteAsync(Path.Combine(staging, "configuration.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { deployment.Id, deployment.Name, deployment.Revision, deployment.Sources, deployment.RegistryCredentialIds }, JsonSerializerOptions.Web), ct);
            ct.ThrowIfCancellationRequested();
            PrivateStorage.RejectLinks(target);
            if (Directory.Exists(target)) Directory.Move(target, previous);
            try { Directory.Move(staging, target); }
            catch
            {
                if (!Directory.Exists(target) && Directory.Exists(previous)) Directory.Move(previous, target);
                throw;
            }
            RemoveDirectory(previous);
        }
        finally { RemoveDirectory(staging); }
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        var configuration = pending ?? throw new IOException("No configuration synchronization is active.");
        var root = Path.Combine(Root, "deployments");
        foreach (var id in configuration.DeploymentIds)
            if (!Directory.Exists(Path.Combine(root, id.ToString("N")))) throw new IOException("Incomplete configuration synchronization.");
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(path), "N", out var id)) throw new IOException("Unexpected configuration cache entry.");
            if (!configuration.DeploymentIds.Contains(id)) RemoveDirectory(path);
        }
        await ReplaceFileAsync("global.env", Encoding.UTF8.GetBytes(configuration.GlobalEnvironment), ct);
        await ReplaceFileAsync("agent.env", Encoding.UTF8.GetBytes(configuration.AgentEnvironment), ct);
        await ReplaceFileAsync("agent.json", JsonSerializer.SerializeToUtf8Bytes(configuration, JsonSerializerOptions.Web), ct);
        ExecutionEnabled = configuration.Policy.ExecutionEnabled;
        pending = null;
    }

    private async Task ReplaceFileAsync(string name, byte[] content, CancellationToken ct)
    {
        var target = Path.Combine(Root, name);
        var temporary = Path.Combine(Root, "replacement");
        PrivateStorage.RejectLinks(target);
        PrivateStorage.RejectLinks(temporary);
        try
        {
            await WriteAsync(temporary, content, ct);
            File.Move(temporary, target, true);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task WriteAsync(string path, byte[] content, CancellationToken ct)
    {
        PrivateStorage.RejectLinks(path);
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await using var file = new FileStream(path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write,
            Share = FileShare.None, UnixCreateMode = PrivateStorage.FileMode });
        File.SetUnixFileMode(path, PrivateStorage.FileMode);
        await file.WriteAsync(content, ct);
        await file.FlushAsync(ct);
        file.Flush(true);
    }

    private static void ValidateEnvironment(string? text)
    {
        if (text is null || text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > 1024 * 1024)
            throw new IOException("Invalid synchronized environment.");
    }

    private static void RemoveDirectory(string path)
    {
        PrivateStorage.RejectLinks(path);
        if (!Directory.Exists(path)) return;
        CheckDirectory(path);
        Directory.Delete(path, true);
        static void CheckDirectory(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                PrivateStorage.RejectLinks(entry);
                if (Directory.Exists(entry)) CheckDirectory(entry);
            }
        }
    }
}
