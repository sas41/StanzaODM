using System.Collections.ObjectModel;
using System.Text;

namespace Stanza.Core.Deployments;

public enum EnvironmentScope { Global, Agent, Deployment }
public enum ProcessEnvironmentInheritance { None }

/// <summary>Exact authored text. Materialize outside the managed workspace; do not concatenate or rewrite it.</summary>
public sealed class EnvironmentFileInput
{
    internal EnvironmentFileInput(EnvironmentScope scope, string fileName, string rawText)
    {
        _ = new UTF8Encoding(false, true).GetByteCount(rawText);
        Scope = scope;
        FileName = fileName;
        RawText = rawText;
    }

    public EnvironmentScope Scope { get; }
    public string FileName { get; }
    public string RawText { get; }
    public byte[] GetUtf8Bytes() => new UTF8Encoding(false, true).GetBytes(RawText);
    public override string ToString() => $"{Scope} environment input (contents redacted)";
}

/// <summary>
/// Immutable operation inputs, not a .env interpreter. The adapter must supply ALL three files with
/// repeated explicit --env-file arguments in this order, use the single explicit Compose file,
/// and clear inherited process environment (including COMPOSE_*). Do not additionally load a workspace .env.
/// Docker Compose resolves all quoting, interpolation, escaping, multiline and cross-file references.
/// Service env_file/container environment are separate and are not synthesized here.
/// Requires a Compose version supporting ordered multiple --env-file inputs; capability-check before execution.
/// </summary>
public sealed class EnvironmentScopeSnapshot
{
    private EnvironmentScopeSnapshot(DeploymentRevision revision, string globalText, string agentText)
    {
        DeploymentId = revision.DeploymentId;
        TargetAgentId = revision.TargetAgentId;
        DeploymentRevision = revision.Number;
        OrderedInputs = Array.AsReadOnly(new[]
        {
            new EnvironmentFileInput(EnvironmentScope.Global, "global.env", globalText),
            new EnvironmentFileInput(EnvironmentScope.Agent, "agent.env", agentText),
            new EnvironmentFileInput(EnvironmentScope.Deployment, "deployment.env", revision.EnvironmentText)
        });
    }

    public Guid DeploymentId { get; }
    public Guid TargetAgentId { get; }
    public long DeploymentRevision { get; }
    public ProcessEnvironmentInheritance ProcessEnvironment => ProcessEnvironmentInheritance.None;
    public ReadOnlyCollection<EnvironmentFileInput> OrderedInputs { get; }
    public string GlobalRawText => OrderedInputs[0].RawText;
    public string AgentRawText => OrderedInputs[1].RawText;
    public string DeploymentRawText => OrderedInputs[2].RawText;

    public static EnvironmentScopeSnapshot Capture(DeploymentRevision revision, string globalText, string agentText)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(globalText);
        ArgumentNullException.ThrowIfNull(agentText);
        return new(revision, globalText, agentText);
    }

    public override string ToString() => $"Environment inputs for {DeploymentId}, revision {DeploymentRevision} (contents redacted)";
}

public sealed record EnvironmentPreviewDiagnostic(EnvironmentScope Scope, int Line, string Message);
public sealed record EnvironmentKeyOccurrence(EnvironmentScope Scope, int Line);

/// <summary>A literal-only preview value. Callers must separately authorize any value reveal.</summary>
public sealed class EnvironmentPreviewValue
{
    internal EnvironmentPreviewValue(string key, string value, EnvironmentKeyOccurrence[] occurrences)
    {
        Key = key;
        Value = value;
        Occurrences = Array.AsReadOnly(occurrences);
    }

    public string Key { get; }
    public string Value { get; }
    public ReadOnlyCollection<EnvironmentKeyOccurrence> Occurrences { get; }
    public EnvironmentScope Source => Occurrences[^1].Scope;
    public override string ToString() => $"{Key} from {Source} (value redacted)";
}

public sealed class EnvironmentPreview
{
    internal EnvironmentPreview(Dictionary<string, EnvironmentPreviewValue> values,
        EnvironmentPreviewDiagnostic[] diagnostics)
    {
        Values = new ReadOnlyDictionary<string, EnvironmentPreviewValue>(values);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public bool IsSupported => Diagnostics.Count == 0;
    public ReadOnlyDictionary<string, EnvironmentPreviewValue> Values { get; }
    public ReadOnlyCollection<EnvironmentPreviewDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Conservative literal preview ONLY, explicitly not Compose-compatible parsing. Accepts ASCII identifier keys,
/// exact KEY=literal assignments (including empty), blank lines and whole-line comments. Refuses quotes,
/// escapes, interpolation (including $$), inline comments, whitespace around assignments, duplicate keys
/// within a scope, BOM, non-ASCII whitespace and non-LF/CRLF line endings. Any refusal suppresses ALL effective values, since later
/// lines may be multiline content or affect earlier expressions. Runtime inputs remain unchanged and may still
/// be valid Compose input. Full semantic preview requires invoking the selected Compose runtime in isolation.
/// </summary>
public static class EnvironmentLiteralPreviewScanner
{
    public static EnvironmentPreview Scan(EnvironmentScopeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var diagnostics = new List<EnvironmentPreviewDiagnostic>();
        var values = new Dictionary<string, (string Value, List<EnvironmentKeyOccurrence> Occurrences)>(StringComparer.Ordinal);
        foreach (var input in snapshot.OrderedInputs)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var lines = input.RawText.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                // A CR is a line ending only when followed by the LF that Split consumed.
                if (index < lines.Length - 1 && line.EndsWith('\r')) line = line[..^1];
                if (line.Any(c => (char.IsControl(c) && c != '\t') ||
                                  (char.IsWhiteSpace(c) && c is not ' ' and not '\t')) || line.Contains('\uFEFF'))
                {
                    Reject("Unsupported whitespace, control character, BOM, or line ending.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart(' ', '\t').StartsWith('#')) continue;
                var equals = line.IndexOf('=');
                if (equals <= 0 || !IsKey(line.AsSpan(0, equals)))
                {
                    Reject("Preview requires an exact ASCII KEY=value assignment.");
                    continue;
                }
                var key = line[..equals];
                var value = line[(equals + 1)..];
                if (value.IndexOfAny(['$', '\\', '\'', '"', '#']) >= 0 || value != value.Trim() || value.Contains('\t'))
                {
                    Reject("Compose syntax requires runtime evaluation; literal preview is unavailable.");
                    continue;
                }
                if (!seen.Add(key))
                {
                    Reject("Duplicate keys within one scope require runtime evaluation.");
                    continue;
                }
                if (!values.TryGetValue(key, out var current)) current = (value, []);
                current.Occurrences.Add(new(input.Scope, index + 1));
                values[key] = (value, current.Occurrences);

                void Reject(string message) => diagnostics.Add(new(input.Scope, index + 1, message));
            }
        }

        var result = new Dictionary<string, EnvironmentPreviewValue>(StringComparer.Ordinal);
        if (diagnostics.Count == 0)
            foreach (var pair in values)
                result.Add(pair.Key, new(pair.Key, pair.Value.Value, pair.Value.Occurrences.ToArray()));
        return new(result, diagnostics.ToArray());
    }

    private static bool IsKey(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty || !(char.IsAsciiLetter(key[0]) || key[0] == '_')) return false;
        foreach (var c in key[1..])
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }
}
