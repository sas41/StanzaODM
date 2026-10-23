using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Stanza.Agent.Runtime;

public sealed record AgentHostPathMap(string AgentRoot, string HostRoot)
{
    public string Translate(string path, string workspace)
    {
        var absolute = Path.GetFullPath(path, workspace);
        var root = Path.GetFullPath(AgentRoot).TrimEnd('/');
        if (absolute != root && !absolute.StartsWith(root + "/", StringComparison.Ordinal)) return path;
        return Path.GetFullPath(HostRoot).TrimEnd('/') + absolute[root.Length..];
    }
}

public interface IAgentHostPathResolver
{
    Task<AgentHostPathMap> ResolveAsync(string dataRoot, IReadOnlyDictionary<string, string> environment, CancellationToken ct);
}

public sealed class DockerAgentHostPathResolver(IProcessRunner runner, IOptions<DockerRuntimeOptions> docker,
    string? containerId = null) : IAgentHostPathResolver
{
    public async Task<AgentHostPathMap> ResolveAsync(string dataRoot, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        // Docker's default container hostname is its short ID, including with the optional host PID profile.
        // Never take this identifier or the host mount path from deployment files/parameters.
        var self = containerId ?? System.Environment.MachineName;
        if (containerId is null && (self.Length is < 12 or > 64 || !self.All(char.IsAsciiHexDigit)))
            throw new IOException("Automatic host-path resolution requires the default Docker container hostname.");
        if (self.Length is < 1 or > 256 || self.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new IOException("Agent container identity is unavailable for host-path resolution.");
        var result = await runner.RunAsync(new(docker.Value.ExecutablePath, dataRoot,
            ["inspect", "--type", "container", "--format", "{{json .Mounts}}", "--", self], environment,
            TimeSpan.FromSeconds(10)), ct);
        if (result.ExitCode != 0 || result.TimedOut || result.OutputLimitExceeded)
            throw new IOException("Agent data mount could not be inspected on the selected Docker daemon.");
        return FromMounts(dataRoot, result.StandardOutput);
    }

    public static AgentHostPathMap FromMounts(string dataRoot, string json)
    {
        var root = Path.GetFullPath(dataRoot).TrimEnd('/');
        if (root.Length == 0) throw new IOException("Agent data root must be a dedicated directory.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 1024)
            throw new IOException("Invalid Agent mount metadata.");
        var matches = new List<string>();
        foreach (var mount in document.RootElement.EnumerateArray())
        {
            var destination = mount.GetProperty("Destination").GetString() ?? "";
            if (destination.StartsWith(root + "/", StringComparison.Ordinal))
                throw new IOException("Nested Agent data mounts are unsupported.");
            if (destination.TrimEnd('/') != root) continue;
            var source = mount.GetProperty("Source").GetString();
            var type = mount.GetProperty("Type").GetString();
            if (type is not ("bind" or "volume") || mount.GetProperty("RW").ValueKind != JsonValueKind.True ||
                string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source) || source.Any(char.IsControl) || Path.GetFullPath(source) == "/")
                throw new IOException("Agent data must have one writable dedicated Docker mount.");
            matches.Add(Path.GetFullPath(source));
        }
        if (matches.Count != 1) throw new IOException("Agent data mount is missing or ambiguous on the selected Docker daemon.");
        return new(root, matches[0]);
    }
}

public static class AgentHostPathOverrides
{
    // `compose config` escapes literal dollars so its output can itself be used as Compose input.
    public static string RenderedPath(string path) => path.Replace("$$", "$", StringComparison.Ordinal);

    public static bool NeedsResolution(JsonElement configuration, string workspace, string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot).TrimEnd('/');
        bool Managed(string path)
        {
            var absolute = Path.GetFullPath(RenderedPath(path), workspace);
            return absolute == root || absolute.StartsWith(root + "/", StringComparison.Ordinal);
        }
        foreach (var service in configuration.GetProperty("services").EnumerateObject())
            if (service.Value.TryGetProperty("volumes", out var mounts))
                foreach (var mount in mounts.EnumerateArray())
                    if (mount.TryGetProperty("type", out var type) && type.GetString() == "bind" &&
                        mount.TryGetProperty("source", out var source) && source.GetString() is { } path && Managed(path)) return true;
        foreach (var kind in new[] { "configs", "secrets" })
            if (configuration.TryGetProperty(kind, out var definitions))
                foreach (var definition in definitions.EnumerateObject())
                    if (definition.Value.TryGetProperty("file", out var source) && source.GetString() is { } path && Managed(path)) return true;
        return false;
    }

    public static JsonObject Create(JsonElement configuration, string workspace, AgentHostPathMap paths)
    {
        var result = new JsonObject();
        var services = new JsonObject();
        foreach (var service in configuration.GetProperty("services").EnumerateObject())
        {
            var volumes = new JsonArray();
            if (service.Value.TryGetProperty("volumes", out var mounts))
                foreach (var mount in mounts.EnumerateArray())
                {
                    if (!mount.TryGetProperty("type", out var type) || type.GetString() != "bind" ||
                        !mount.TryGetProperty("source", out var value) || value.GetString() is not { } source) continue;
                    var decoded = RenderedPath(source);
                    var mapped = paths.Translate(decoded, workspace);
                    if (mapped == decoded) continue;
                    var copy = JsonNode.Parse(mount.GetRawText())!.AsObject();
                    DecodeRenderedStrings(copy);
                    copy["source"] = mapped;
                    volumes.Add(copy);
                }
            if (volumes.Count > 0) services[service.Name] = new JsonObject { ["volumes"] = volumes };
        }
        if (services.Count > 0) result["services"] = services;
        foreach (var kind in new[] { "configs", "secrets" })
        {
            if (!configuration.TryGetProperty(kind, out var definitions)) continue;
            var mappedDefinitions = new JsonObject();
            foreach (var definition in definitions.EnumerateObject())
            {
                if (!definition.Value.TryGetProperty("file", out var file) || file.GetString() is not { } source) continue;
                var decoded = RenderedPath(source);
                var mapped = paths.Translate(decoded, workspace);
                if (mapped != decoded) mappedDefinitions[definition.Name] = new JsonObject { ["file"] = mapped };
            }
            if (mappedDefinitions.Count > 0) result[kind] = mappedDefinitions;
        }
        return result;
    }

    public static string Serialize(JsonObject overrides) => overrides.ToJsonString().Replace("$", "$$", StringComparison.Ordinal);

    private static void DecodeRenderedStrings(JsonNode node)
    {
        if (node is JsonObject properties)
        {
            foreach (var key in properties.Select(p => p.Key).ToArray())
                if (properties[key] is JsonValue value && value.TryGetValue<string>(out var text)) properties[key] = RenderedPath(text);
                else if (properties[key] is { } child) DecodeRenderedStrings(child);
        }
        else if (node is JsonArray items)
        {
            for (var i = 0; i < items.Count; i++)
                if (items[i] is JsonValue value && value.TryGetValue<string>(out var text)) items[i] = RenderedPath(text);
                else if (items[i] is { } child) DecodeRenderedStrings(child);
        }
    }
}
