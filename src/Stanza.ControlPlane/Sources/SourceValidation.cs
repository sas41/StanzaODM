using System.Net;
using Stanza.Core.Deployments;

namespace Stanza.ControlPlane.Sources;

public static class SourceValidation
{
    // Network requests run only on the explicitly selected, approved Agent. Intranet hosts are intentional;
    // loopback/link-local/metadata addresses and credential-bearing URLs are never accepted.
    public static string Url(string value, bool registry = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            value.Contains('\\') || value.StartsWith('-')) throw new SourceException("Supply an HTTPS endpoint without credentials.");
        if (registry && !value.Contains("://")) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.TrimEnd('.').EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.TrimEnd('.').Equals("localhost.localdomain", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.TrimEnd('.').Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            throw new SourceException("Only HTTPS URLs without embedded credentials, queries, or fragments are supported.");
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var bytes = ip.GetAddressBytes();
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast ||
                (bytes.Length == 4 && (bytes[0] == 0 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] >= 224)))
                throw new SourceException("Loopback, link-local, metadata, and multicast endpoints are not allowed.");
        }
        if (registry && uri.AbsolutePath != "/") throw new SourceException("Registry endpoints must be a host with an optional port, without a repository path.");
        return registry ? uri.Authority : uri.AbsoluteUri;
    }

    public static string Reference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.StartsWith('-') || value.StartsWith('/') || value.EndsWith('/') ||
            value.EndsWith('.') || value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || value.Contains("..") || value.Contains("@{") ||
            value.Contains("//") || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "~^:?*[\\".Contains(c)) ||
            value == "@" || value.Split('/').Any(x => x.StartsWith('.') || x.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
            throw new SourceException("Supply a valid branch, tag, full ref, or commit hash (not a revision expression).");
        return value;
    }

    public static DeploymentPath Destination(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Split('/').Any(x => x.Equals(".git", StringComparison.OrdinalIgnoreCase) || x.StartsWith(".stanza", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException();
            return DeploymentPath.Parse(value);
        }
        catch (ArgumentException) { throw new SourceException("Choose a safe relative checkout destination, outside reserved .git/.stanza paths."); }
    }
}
