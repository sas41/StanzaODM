using System.Buffers.Binary;
using Stanza.Agent.Terminals;

namespace Stanza.Agent.Logs;

/// <summary>Read-only Docker follow stream. HttpClient removes HTTP chunking; this removes Docker multiplexing.</summary>
public sealed class DockerLogStream : IAsyncDisposable
{
    public const int MaximumDockerFrameBytes = 16 * 1024 * 1024;
    private readonly HttpResponseMessage response;
    private readonly Stream stream;
    private readonly bool tty;
    private readonly byte[] header = new byte[8];
    private int remaining;

    private DockerLogStream(HttpResponseMessage response, Stream stream, bool tty)
    { this.response = response; this.stream = stream; this.tty = tty; }

    public static async Task<DockerLogStream> OpenAsync(HttpClient http, DockerContainerTarget target, CancellationToken token)
    {
        if (!DockerTerminalClient.SafeId(target.Id)) throw new IOException("Invalid log container identifier.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"containers/{target.Id}/logs?follow=1&stdout=1&stderr=1&timestamps=1&tail=200");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            if (!response.IsSuccessStatusCode) throw new IOException("Docker logs API rejected the request.");
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("application/vnd.docker.raw-stream" or "application/vnd.docker.multiplexed-stream"))
                throw new IOException("Docker returned an unsupported log stream.");
            var stream = await response.Content.ReadAsStreamAsync(token);
            return new(response, stream, target.Tty);
        }
        catch { response.Dispose(); throw; }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
    {
        if (buffer.Length is 0 or > AgentTerminalManager.MaximumChunk) throw new ArgumentOutOfRangeException(nameof(buffer));
        if (tty) return await stream.ReadAsync(buffer, token);
        var emptyFrames = 0;
        while (remaining == 0)
        {
            var first = await stream.ReadAsync(header.AsMemory(0, 1), token);
            if (first == 0) return 0;
            try { await stream.ReadExactlyAsync(header.AsMemory(1), token); }
            catch (EndOfStreamException) { throw new IOException("Truncated Docker log header."); }
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (header[0] is not (1 or 2) || header[1] != 0 || header[2] != 0 || header[3] != 0 || length > MaximumDockerFrameBytes)
                throw new IOException("Invalid Docker log frame.");
            remaining = (int)length;
            if (remaining == 0 && ++emptyFrames > 64) throw new IOException("Too many empty Docker log frames.");
        }
        var count = await stream.ReadAsync(buffer[..Math.Min(buffer.Length, remaining)], token);
        if (count == 0) throw new IOException("Truncated Docker log payload.");
        remaining -= count;
        return count;
    }

    public async ValueTask DisposeAsync()
    {
        try { await stream.DisposeAsync(); }
        finally { response.Dispose(); }
    }
}
