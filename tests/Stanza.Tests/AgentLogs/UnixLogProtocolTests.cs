using System.Net.Sockets;
using System.Text;
using Stanza.Agent.Terminals;
using Stanza.Core.Contracts;

namespace Stanza.Tests.AgentLogs;

public sealed class UnixLogProtocolTests
{
    [Fact]
    public async Task StoppedServiceUsesAllContainersAndChunkedHttpIsDecodedBeforeDockerFraming()
    {
        var path = Path.Combine("/tmp/opencode", "stanza-log-socket-" + Guid.NewGuid().ToString("N") + ".sock");
        var deployment = Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(4);
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 3; index++)
            {
                using var socket = await listener.AcceptAsync(timeout.Token);
                await using var stream = new NetworkStream(socket);
                var request = await ReadHeaderAsync(stream, timeout.Token);
                Assert.StartsWith("GET ", request); Assert.DoesNotContain("/exec", request);
                if (index == 0)
                {
                    Assert.StartsWith("GET /containers/json?all=1&filters=", request);
                    var decoded = Uri.UnescapeDataString(request);
                    Assert.Contains("com.docker.compose.project=stanza-" + deployment.ToString("N"), decoded);
                    Assert.Contains("com.docker.compose.service=app", decoded);
                    await JsonAsync(stream, "[{\"Id\":\"safe-id\"}]", timeout.Token);
                }
                else if (index == 1)
                {
                    Assert.StartsWith("GET /containers/safe-id/json ", request);
                    await JsonAsync(stream, "{\"Id\":\"safe-id\",\"State\":{\"Running\":false},\"Config\":{\"Tty\":false,\"Labels\":{\"com.docker.compose.project\":\"stanza-" + deployment.ToString("N") + "\",\"com.docker.compose.service\":\"app\"}}}", timeout.Token);
                }
                else
                {
                    Assert.StartsWith("GET /containers/safe-id/logs?follow=1&stdout=1&stderr=1&timestamps=1&tail=200 ", request);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/vnd.docker.raw-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"), timeout.Token);
                    var payload = DockerLogStreamTests.Frame(1, "numbered-stdout-1\n").Concat(DockerLogStreamTests.Frame(2, "numbered-stderr-2\n")).ToArray();
                    for (var position = 0; position < payload.Length; position += 3)
                    {
                        var length = Math.Min(3, payload.Length - position);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(length.ToString("X") + "\r\n"), timeout.Token);
                        await stream.WriteAsync(payload.AsMemory(position, length), timeout.Token);
                        await stream.WriteAsync("\r\n"u8.ToArray(), timeout.Token);
                    }
                    await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), timeout.Token);
                }
            }
        });
        try
        {
            using var client = new DockerTerminalClient("unix://" + path);
            var target = await client.ResolveTargetAsync(deployment, new("ignored", [], Service: "app", Mode: "logs"), false, timeout.Token);
            Assert.Equal("safe-id", target.Id); Assert.False(target.Tty);
            await using var logs = await client.OpenLogsAsync(target, timeout.Token);
            Assert.Equal("numbered-stdout-1\nnumbered-stderr-2\n", await DockerLogStreamTests.ReadAllAsync(logs));
            await server;
        }
        finally { File.Delete(path); }
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream(); var one = new byte[1];
        while (bytes.Length < 8192)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Truncated HTTP request.");
            bytes.WriteByte(one[0]);
            if (bytes.Length >= 4 && Encoding.ASCII.GetString(bytes.GetBuffer(), (int)bytes.Length - 4, 4) == "\r\n\r\n")
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new IOException("Oversized HTTP request.");
    }
    private static async Task JsonAsync(Stream stream, string json, CancellationToken token) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}"), token);
}
