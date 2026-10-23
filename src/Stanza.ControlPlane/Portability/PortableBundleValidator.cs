using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Stanza.Core.Deployments;
using Stanza.ControlPlane.Deployments;

namespace Stanza.ControlPlane.Portability;

public static class PortableBundleValidator
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static PortableBundle Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateProperties(document.RootElement);
            var bundle = JsonSerializer.Deserialize<PortableBundle>(bytes, JsonOptions)
                ?? throw new PortabilityException("A bundle object is required.");
            Validate(bundle);
            return bundle;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or NullReferenceException)
        { throw new PortabilityException("Invalid portable bundle schema, paths, text, references, or file limits."); }
    }

    public static void Validate(PortableBundle b)
    {
        try { ValidateCore(b); }
        catch (Exception error) when (error is ArgumentException or FormatException or NullReferenceException)
        { throw new PortabilityException("Invalid portable bundle paths, source collisions, text, references, or file limits."); }
    }

    private static void ValidateCore(PortableBundle b)
    {
        if (b.Version != 1) throw new PortabilityException("Unsupported bundle version; expected version 1.");
        if (b.Deployments is null || b.Agents is null || b.Sections is null || b.Deployments.Count > 100 || b.Agents.Count > 100 || b.Sections.Count > 32)
            throw new PortabilityException("Bundle collection limits exceeded.");
        Unique(b.Deployments.Select(x => x.Id));
        Unique(b.Agents.Select(x => x.Id));
        Unique(b.Sections.Select(x => x.Kind));
        if (b.References is null || b.References.Count > 2000) throw new PortabilityException("Resource reference limits exceeded.");
        Unique(b.Deployments.Select(x => x.Id).Concat(b.Agents.Select(x => x.Id)).Concat(b.References.Select(x => x.Id)));
        foreach (var reference in b.References)
        {
            if (reference.Type is not ("deployment" or "credential" or "action")) throw new PortabilityException("Unsupported portable resource type.");
            Text(reference.Name, 100);
        }
        foreach (var a in b.Agents) Text(a.Label, 253);
        foreach (var d in b.Deployments)
        {
            Text(d.Name, 100);
            if (string.IsNullOrWhiteSpace(d.Name)) throw new PortabilityException("Deployment names cannot be empty.");
            if (d.Agent is not null && !b.Agents.Any(a => a.Id == d.Agent)) throw new PortabilityException("An Agent reference is missing.");
            Text(d.Compose, int.MaxValue);
            Text(d.Environment, int.MaxValue);
            if (d.Files is null) throw new PortabilityException("Managed files are required.");
            var files = new List<ManagedFileSnapshot>
            {
                ManagedFileSnapshot.Text("compose.yaml", d.Compose), ManagedFileSnapshot.Text(".env", d.Environment)
            };
            foreach (var f in d.Files)
            {
                if (f.Path is null || f.Path.Length > 1024) throw new PortabilityException("Invalid file path length.");
                if (f.Kind == "text" && f.Text is not null && f.Base64 is null)
                {
                    Text(f.Text, int.MaxValue);
                    files.Add(ManagedFileSnapshot.Text(f.Path, f.Text));
                }
                else if (f.Kind == "binary" && f.Text is null && f.Base64 is not null)
                    files.Add(ManagedFileSnapshot.Binary(f.Path, Convert.FromBase64String(f.Base64)));
                else if (f.Kind == "directory" && f.Text is null && f.Base64 is null)
                    files.Add(ManagedFileSnapshot.Directory(f.Path));
                else throw new PortabilityException("Use plaintext text files or bounded base64 binary files, never links.");
            }
            if (d.SourceDestinations is null || d.SourceDestinations.Count > 32) throw new PortabilityException("Too many source workspace reservations.");
            // Destination application services enforce the current configured file limits, including zero/unlimited.
            DeploymentWorkspaceValidator.Validate(files, d.SourceDestinations.Select(path => new GitSourceWorkspace(Guid.NewGuid(), path)).ToArray(), new(0, 0, 0));
        }
    }

    public static IReadOnlyList<string> Warnings(string compose) =>
    [
        "Persistent workload data is NOT transferred. Host bind mounts and existing volumes need separate preparation. Transfer persistent data is not yet available.",
        "Published ports, explicit container names, networks, bind mounts, existing volumes, architecture and effective inherited environment require destination validation. This text scan does not prove absence of conflicts.",
        .. Regex.IsMatch(compose, @"(?im)^\s*(ports|container_name|networks|volumes|platform)\s*:|(?:\./|\.\./|/[^\s]+:)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            ? new[] { "Potential destination-specific ports, names, networks, storage, or platform declarations were detected in Compose. Review the exact original text." } : Array.Empty<string>()
    ];

    private static void Unique(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || Guid.TryParse(id, out _) || !Regex.IsMatch(id, "^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) || !seen.Add(id))
                throw new PortabilityException("Logical IDs must be unique safe names of at most 100 characters.");
    }
    private static void Text(string text, int maximum)
    {
        if (text is null || text.Contains('\0') || Utf8.GetByteCount(text) > maximum) throw new PortabilityException("Text is missing, invalid, or exceeds its limit.");
    }
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new PortabilityException("Duplicate JSON properties are not supported.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
