namespace Stanza.Core.Deployments;

/// <summary>A portable, unambiguous path inside a managed workspace, never a host path.</summary>
public sealed record DeploymentPath
{
    private DeploymentPath(string value) => Value = value;

    public string Value { get; }

    public static DeploymentPath Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.StartsWith('/') || value.Contains('\\') || value.Any(char.IsControl))
            throw new ArgumentException("Use a relative path with forward slashes only.", nameof(value));

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
                throw new ArgumentException("Path contains an unsafe or ambiguous segment.", nameof(value));

            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                      stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
                throw new ArgumentException("Path contains a reserved device name.", nameof(value));
        }

        return new DeploymentPath(value);
    }

    public bool IsSameOrAncestorOf(DeploymentPath other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Value.Equals(other.Value, StringComparison.OrdinalIgnoreCase) ||
               other.Value.StartsWith(Value + "/", StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Value;
}
