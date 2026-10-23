using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stanza.Core.Automation;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Runtime;

/// <summary>Distribution API tag discovery. No redirects, ambient credentials, or credential-bearing diagnostics.</summary>
public sealed partial class RegistryTagClient(HttpClient http)
{
    [GeneratedRegex("([a-zA-Z]+)=\"([^\"]*)\"")]
    private static partial Regex ChallengeFields();

    public async Task<IReadOnlyList<string>> ListAsync(string repository, IReadOnlyList<AgentRegistryCredential> credentials, CancellationToken ct)
    {
        var (host, path) = ActionInputRules.Repository(repository);
        var origin = new Uri("https://" + host);
        var credential = credentials.SingleOrDefault(c =>
        {
            var endpoint = c.Server.Contains("://", StringComparison.Ordinal) ? c.Server : "https://" + c.Server;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
            var server = uri.Authority is "docker.io" or "index.docker.io" ? "registry-1.docker.io" : uri.Authority;
            return server == host;
        });
        AuthenticationHeaderValue? authorization = null;
        var tags = new HashSet<string>(StringComparer.Ordinal);
        Uri? next = new(origin, "/v2/" + path + "/tags/list?n=1000");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null)
        {
            if (!visited.Add(next.AbsoluteUri) || visited.Count > 100) throw new IOException("Registry pagination exceeded its limit.");
            using var initial = await SendAsync(next, authorization, ct);
            HttpResponseMessage response = initial;
            try
            {
                if (initial.StatusCode == HttpStatusCode.Unauthorized)
                {
                    authorization = await AuthenticateAsync(origin, path, initial, credential, ct);
                    response = await SendAsync(next, authorization, ct);
                }
                if (!response.IsSuccessStatusCode) throw new IOException("Registry tag listing is unavailable or denied.");
                using var document = JsonDocument.Parse(await BodyAsync(response, ct));
                if (!document.RootElement.TryGetProperty("tags", out var values)) throw new IOException("Invalid registry tag listing.");
                if (values.ValueKind != JsonValueKind.Null)
                    foreach (var tag in values.EnumerateArray())
                    {
                        var value = tag.GetString() ?? "";
                        if (!ActionInputRules.IsTag(value)) throw new IOException("Invalid registry tag.");
                        tags.Add(value);
                        if (tags.Count > 10000) throw new IOException("Registry has more than 10000 tags; narrow the repository.");
                    }
                next = null;
                if (response.Headers.TryGetValues("Link", out var links))
                    foreach (var link in links.SelectMany(x => x.Split(',')))
                    {
                        if (!link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase) && !link.Contains("rel=next", StringComparison.OrdinalIgnoreCase)) continue;
                        var start = link.IndexOf('<'); var end = link.IndexOf('>');
                        if (start < 0 || end <= start || !Uri.TryCreate(origin, link[(start + 1)..end], out var candidate) ||
                            candidate.Scheme != "https" || candidate.Authority != origin.Authority || candidate.UserInfo.Length != 0 ||
                            candidate.AbsolutePath != "/v2/" + path + "/tags/list") throw new IOException("Invalid registry pagination link.");
                        next = candidate;
                    }
            }
            finally { if (!ReferenceEquals(response, initial)) response.Dispose(); }
        }
        return tags.OrderDescending(StringComparer.Ordinal).ToArray();
    }

    private async Task<AuthenticationHeaderValue> AuthenticateAsync(Uri origin, string repository, HttpResponseMessage response,
        AgentRegistryCredential? credential, CancellationToken ct)
    {
        var challenge = response.Headers.WwwAuthenticate.FirstOrDefault(x => x.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        if (challenge is null)
        {
            if (credential is null || !response.Headers.WwwAuthenticate.Any(x => x.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Registry authentication is unavailable.");
            return Basic(credential);
        }
        var fields = ChallengeFields().Matches(challenge.Parameter ?? "").ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("realm", out var realm) || !Uri.TryCreate(realm, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Authority != origin.Authority && !(origin.Host == "registry-1.docker.io" && uri.Authority == "auth.docker.io")))
            throw new IOException("Registry authentication endpoint is not trusted.");
        var tokenUri = new Uri(uri.AbsoluteUri + "?service=" + Uri.EscapeDataString(fields.GetValueOrDefault("service", origin.Authority)) +
            "&scope=" + Uri.EscapeDataString("repository:" + repository + ":pull"));
        using var token = await SendAsync(tokenUri, credential is null ? null : Basic(credential), ct);
        if (!token.IsSuccessStatusCode) throw new IOException("Registry authentication failed.");
        using var document = JsonDocument.Parse(await BodyAsync(token, ct));
        var root = document.RootElement;
        var value = root.TryGetProperty("token", out var t) ? t.GetString() : root.TryGetProperty("access_token", out t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(value) || value.Length > 65536 || value.Any(char.IsControl)) throw new IOException("Invalid registry token.");
        return new("Bearer", value);
    }

    private static AuthenticationHeaderValue Basic(AgentRegistryCredential credential) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Username + ":" + credential.Password)));

    private async Task<HttpResponseMessage> SendAsync(Uri uri, AuthenticationHeaderValue? authorization, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = authorization;
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<byte[]> BodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
