using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Terminals;

/// <summary>Unix-only Docker API client. Hijacked connections never enter an HTTP connection pool.</summary>
public sealed class DockerTerminalClient : IDisposable
{
    private readonly string path;
    private readonly HttpClient http;
    public DockerTerminalClient(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "unix" ||
            !endpoint.StartsWith("unix:///", StringComparison.Ordinal) || uri.Host.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath == "/" || endpoint.Any(char.IsControl))
            throw new IOException("Only a local Unix Docker socket supports terminals.");
        path = uri.LocalPath;
        http = new(new SocketsHttpHandler { ConnectCallback = async (_, ct) => await ConnectAsync(ct) })
        { BaseAddress = new Uri("http://docker/"), Timeout = TimeSpan.FromSeconds(15) };
    }
    private async Task<Stream> ConnectAsync(CancellationToken token)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token); return new NetworkStream(socket, true); }
        catch { socket.Dispose(); throw; }
    }
    public async Task<JsonDocument> JsonAsync(HttpMethod method, string route, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new IOException("Docker terminal API rejected the request.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        { if (data.Length + read > 1024 * 1024) throw new IOException("Docker response exceeds limit."); await data.WriteAsync(buffer.AsMemory(0, read), token); }
        return JsonDocument.Parse(data.ToArray());
    }
    public async Task PostAsync(string route, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new IOException("Docker terminal API rejected the request.");
    }
    public static bool SafeId(string? id) => id is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    public async Task<string> ResolveAsync(Guid deployment, AgentExecution execution, CancellationToken token)
    {
        if (deployment == Guid.Empty || (execution.Service is null) == (execution.Container is null) ||
            execution.Service is not null && !SafeId(execution.Service) || execution.Container is not null && !SafeId(execution.Container))
            throw new IOException("A scoped container target is required.");
        var project = "stanza-" + deployment.ToString("N");
        var container = execution.Container;
        if (container is null)
        {
            var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new { label = new[] { "com.docker.compose.project=" + project, "com.docker.compose.service=" + execution.Service } }));
            using var list = await JsonAsync(HttpMethod.Get, "containers/json?filters=" + filters, null, token);
            if (list.RootElement.GetArrayLength() != 1) throw new IOException("Select exactly one running container.");
            container = list.RootElement[0].GetProperty("Id").GetString();
        }
        if (!SafeId(container)) throw new IOException("Invalid Docker container identifier.");
        using var inspect = await JsonAsync(HttpMethod.Get, "containers/" + container + "/json", null, token);
        var root = inspect.RootElement;
        if (!root.GetProperty("State").GetProperty("Running").GetBoolean() ||
            !root.GetProperty("Config").GetProperty("Labels").TryGetProperty("com.docker.compose.project", out var label) || label.GetString() != project ||
            execution.Service is not null && (!root.GetProperty("Config").GetProperty("Labels").TryGetProperty("com.docker.compose.service", out var service) || service.GetString() != execution.Service))
            throw new IOException("Container is not a running member of this deployment.");
        var id = root.GetProperty("Id").GetString();
        return SafeId(id) ? id! : throw new IOException("Invalid Docker container identifier.");
    }
    public async Task<string> CreateAsync(string container, object configuration, CancellationToken token)
    {
        if (!SafeId(container)) throw new IOException("Invalid container identifier.");
        using var result = await JsonAsync(HttpMethod.Post, "containers/" + container + "/exec", configuration, token);
        var id = result.RootElement.GetProperty("Id").GetString();
        return SafeId(id) ? id! : throw new IOException("Invalid exec identifier.");
    }
    public async Task<Stream> StartAsync(string id, CancellationToken token)
    {
        if (!SafeId(id)) throw new IOException("Invalid exec identifier.");
        var stream = await ConnectAsync(token);
        try
        {
            var body = "{\"Detach\":false,\"Tty\":true}";
            var request = Encoding.ASCII.GetBytes($"POST /exec/{id}/start HTTP/1.1\r\nHost: docker\r\nConnection: Upgrade\r\nUpgrade: tcp\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}");
            await stream.WriteAsync(request, token);
            await ReadUpgradeAsync(stream, token);
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    public static async Task ReadUpgradeAsync(Stream stream, CancellationToken token)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (header.Count < 16384)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Truncated Docker upgrade.");
            header.Add(one[0]);
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        if (header.Count >= 16384 || lines.Length < 3 || lines[0] is not ("HTTP/1.1 101 UPGRADED" or "HTTP/1.1 101 Switching Protocols" or "HTTP/1.1 200 OK"))
            throw new IOException("Invalid Docker upgrade response.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(x => x.Length != 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new IOException("Invalid Docker headers.");
        }
        if (headers.ContainsKey("Transfer-Encoding") || headers.ContainsKey("Content-Length") ||
            lines[0].Contains(" 101 ", StringComparison.Ordinal) &&
            (!headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("tcp", StringComparison.OrdinalIgnoreCase) ||
             !headers.TryGetValue("Connection", out var connection) || !connection.Split(',').Any(x => x.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase))) ||
            lines[0].Contains(" 200 ", StringComparison.Ordinal) &&
            (!headers.TryGetValue("Content-Type", out var type) || !type.StartsWith("application/vnd.docker.raw-stream", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Docker did not return a raw upgraded stream.");
    }
    public void Dispose() => http.Dispose();
}
