using System.Net.Sockets;
using System.Text.Json;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Logs;

public sealed class DockerLogClient : IDisposable
{
    private readonly HttpClient http;
    public DockerLogClient(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "unix" ||
            !endpoint.StartsWith("unix:///", StringComparison.Ordinal) || uri.Host.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath == "/" || endpoint.Any(char.IsControl)) throw new IOException("Logs require a local Docker socket.");
        http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(uri.LocalPath), ct); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            } }) { BaseAddress = new Uri("http://docker/"), Timeout = TimeSpan.FromSeconds(15) };
    }
    private async Task<JsonDocument> JsonAsync(string route, CancellationToken token)
    {
        using var response = await http.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new IOException("Docker logs API rejected the request.");
        await using var input = await response.Content.ReadAsStreamAsync(token); using var data = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        { if (data.Length + read > 1024 * 1024) throw new IOException("Docker response exceeds limit."); await data.WriteAsync(buffer.AsMemory(0, read), token); }
        return JsonDocument.Parse(data.ToArray());
    }
    public async Task<DockerContainerTarget> ResolveTargetAsync(Guid deployment, AgentExecution execution, CancellationToken token)
    {
        if (deployment == Guid.Empty || !AgentLogProtocol.IsValid(execution)) throw new IOException("A scoped log target is required.");
        var project = "stanza-" + deployment.ToString("N"); var container = execution.Container;
        if (container is null)
        {
            var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new { label = new[] { "com.docker.compose.project=" + project, "com.docker.compose.service=" + execution.Service } }));
            using var list = await JsonAsync("containers/json?all=1&filters=" + filters, token);
            if (list.RootElement.GetArrayLength() != 1) throw new IOException("Select exactly one scoped container.");
            container = list.RootElement[0].GetProperty("Id").GetString();
        }
        if (!AgentLogProtocol.IsTargetName(container)) throw new IOException("Invalid container identifier.");
        using var inspect = await JsonAsync("containers/" + container + "/json", token); var root = inspect.RootElement;
        var labels = root.GetProperty("Config").GetProperty("Labels");
        if (!labels.TryGetProperty("com.docker.compose.project", out var owner) || owner.GetString() != project ||
            execution.Service is not null && (!labels.TryGetProperty("com.docker.compose.service", out var service) || service.GetString() != execution.Service))
            throw new IOException("Container is not a member of this deployment.");
        var id = root.GetProperty("Id").GetString();
        if (!AgentLogProtocol.IsTargetName(id)) throw new IOException("Invalid container identifier.");
        return new(id!, root.GetProperty("Config").TryGetProperty("Tty", out var tty) && tty.GetBoolean());
    }
    public Task<DockerLogStream> OpenLogsAsync(DockerContainerTarget target, CancellationToken token) => DockerLogStream.OpenAsync(http, target, token);
    public void Dispose() => http.Dispose();
}

public sealed record DockerContainerTarget(string Id, bool Tty);
