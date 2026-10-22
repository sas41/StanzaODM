using System.Text.Json;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;
using Stanza.Tests.Agent;

namespace Stanza.Tests.ImageProbe;

internal sealed class ImageProbeRunner : IProcessRunner
{
    public static string Sha(char c) => "sha256:" + new string(c, 64);
    public Guid Deployment { get; set; }
    public string ConfigJson { get; set; } = "{\"services\":{\"app\":{\"image\":\"registry.example/app:latest\",\"platform\":\"linux/arm64/v8\"},\"built\":{\"build\":{\"context\":\".\"},\"image\":\"local:built\"}}}";
    public string Current { get; set; } = Sha('a');
    public string Remote { get; set; } = Sha('b');
    public string Manifest { get; set; } = Sha('c');
    public string RuntimePlatform { get; set; } = "linux/amd64";
    public string Os { get; set; } = "linux";
    public string Architecture { get; set; } = "arm64";
    public string? Variant { get; set; } = "v8";
    public bool Installed { get; set; } = true;
    public bool WrongScope { get; set; }
    public bool MixedReplicas { get; set; }
    public bool AmbiguousVariant { get; set; }
    public bool FailRegistry { get; set; }
    public bool ContainerdIdentity { get; set; }
    public string InstalledConfig { get; set; } = Sha('e');
    public string? DescriptorOverride { get; set; }
    public string? RawOverride { get; set; }
    public bool CredentialSeen { get; private set; }
    public List<ProcessRequest> Requests { get; } = [];
    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (request.Environment.TryGetValue("DOCKER_CONFIG", out var auth) && File.Exists(Path.Combine(auth, "config.json")))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(auth, "config.json")));
            CredentialSeen |= document.RootElement.GetProperty("auths").TryGetProperty("registry.example", out _);
        }
        var a = request.Arguments; string output;
        if (a.Contains("config")) output = ConfigJson;
        else if (a[0] == "version") output = RuntimePlatform;
        else if (a.Contains("imagetools"))
        {
            if (FailRegistry) return Task.FromResult(new ProcessResult(1, "secret-registry-response", "private-password", false, false));
            if (a.Contains("--raw")) output = RawOverride ?? JsonSerializer.Serialize(new { config = new { digest = ContainerdIdentity && a[^1].EndsWith("@" + Current, StringComparison.Ordinal) ? InstalledConfig : Remote } });
            else if (a.Contains("{{json .Image}}")) output = JsonSerializer.Serialize(new { os = Os, architecture = Architecture, variant = Variant });
            else if (ContainerdIdentity && a[^1].EndsWith("@" + Current, StringComparison.Ordinal)) output = JsonSerializer.Serialize(new { digest = Current });
            else output = DescriptorOverride ?? JsonSerializer.Serialize(new { digest = Sha('f'), manifests = AmbiguousVariant
                ? new object[] { new { digest = Manifest, platform = new { os = "linux", architecture = "arm64", variant = "v8" } }, new { digest = Sha('d'), platform = new { os = "linux", architecture = "arm64", variant = "v9" } } }
                : new object[] { new { digest = Manifest, platform = new { os = "linux", architecture = "arm64", variant = "v8" } }, new { digest = Sha('e'), platform = new { os = "linux", architecture = "amd64" } } } });
        }
        else if (a[0] == "ps") output = !Installed ? "" : MixedReplicas ? new string('a', 64) + "\n" + new string('b', 64) : new string('a', 64);
        else if (a[0] == "inspect") output = JsonSerializer.Serialize(new { Id = a[^1], Image = MixedReplicas && a[^1][0] == 'b' ? Sha('d') : Current,
            Config = new { Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "stanza-" + (WrongScope ? Guid.NewGuid() : Deployment).ToString("N"),
                ["com.docker.compose.service"] = a.Contains("built") ? "built" : "app", ["com.docker.compose.oneoff"] = "False" } } });
        else if (a[0] == "image") output = JsonSerializer.Serialize(new { Id = Current, Os, Architecture, Variant,
            Descriptor = ContainerdIdentity ? new { digest = Current, mediaType = "application/vnd.oci.image.manifest.v1+json" } : null,
            RepoDigests = new[] { "registry.example/app@" + Manifest } });
        else output = "[]";
        return Task.FromResult(new ProcessResult(0, output, "", false, false));
    }
    public AgentWorkRequest Request(string operation = "image-digests")
    {
        var request = AgentWorkExecutorTests.Request(operation); Deployment = request.DeploymentId;
        return request with { Registries = [new("registry.example", "fixture-user", "private-password")] };
    }
}
