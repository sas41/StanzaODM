using System.Text.RegularExpressions;

namespace Stanza.Core.Automation;

/// <summary>Runtime-independent syntax rules for bounded action selections.</summary>
public static partial class ActionInputRules
{
    [GeneratedRegex(@"^[\w][\w.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPathPattern();

    public static bool IsTag(string value) => value.All(char.IsAscii) && TagPattern().IsMatch(value);
    public static bool IsBranch(string value) => value.Length is > 0 and <= 256 && !value.StartsWith('-') &&
        !value.StartsWith('/') && !value.EndsWith('/') && !value.EndsWith('.') && !value.Contains("..", StringComparison.Ordinal) &&
        !value.Contains("@{", StringComparison.Ordinal) && value != "@" &&
        value.Split('/').All(p => p.Length > 0 && !p.StartsWith('.') && !p.EndsWith(".lock", StringComparison.Ordinal)) &&
        value.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not ('~' or '^' or ':' or '?' or '*' or '[' or '\\'));

    public static (string Host, string Path) Repository(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512 || value.Contains("://", StringComparison.Ordinal) || value.Contains('@'))
            throw new ArgumentException("Specify an image repository without a tag, digest, or URL scheme.");
        var parts = value.Split('/', 2);
        var explicitHost = parts.Length == 2 && (parts[0].Contains('.') || parts[0].Contains(':') || parts[0] == "localhost");
        var host = explicitHost ? parts[0] : "registry-1.docker.io";
        if (host is "docker.io" or "index.docker.io") host = "registry-1.docker.io";
        var path = explicitHost ? parts[1] : value;
        if (host == "registry-1.docker.io" && !path.Contains('/')) path = "library/" + path;
        if (!Uri.TryCreate("https://" + host, UriKind.Absolute, out var uri) || uri.Authority != host || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || !RepositoryPathPattern().IsMatch(path))
            throw new ArgumentException("Invalid image repository.");
        return (host, path);
    }
}
