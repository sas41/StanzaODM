using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Stanza.Agent.Logs;
using Stanza.Agent.Terminals;

namespace Stanza.Tests.AgentLogs;

public sealed class DockerLogStreamTests
{
    [Fact]
    public async Task DecodesFragmentedStdoutAndStderrWithoutLeakingMultiplexHeaders()
    {
        var data = Frame(1, "stdout-1\n").Concat(Frame(2, "stderr-2\n")).Concat(Frame(1, "stdout-3\n")).ToArray();
        using var client = Client(data, false);
        await using var logs = await DockerLogStream.OpenAsync(client, new("safe-container", false), default);
        Assert.Equal("stdout-1\nstderr-2\nstdout-3\n", await ReadAllAsync(logs));
    }

    [Fact]
    public async Task TtyStreamIsRawEvenIfItsBytesLookLikeMultiplexHeaders()
    {
        var bytes = Frame(1, "raw\r\n");
        using var client = Client(bytes, true);
        await using var logs = await DockerLogStream.OpenAsync(client, new("safe-container", true), default);
        var result = await ReadAllBytesAsync(logs);
        Assert.Equal(bytes, result);
    }

    [Fact]
    public async Task FramesLargerThanWireChunksAreIncrementallyDecodedWithin64KiB()
    {
        var payload = Enumerable.Repeat((byte)251, 200000).ToArray();
        using var client = Client(Frame(2, payload), false, fragment: 8191);
        await using var logs = await DockerLogStream.OpenAsync(client, new("safe-container", false), default);
        Assert.Equal(payload, await ReadAllBytesAsync(logs));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(3, 0, 0, 0, 0)]
    [InlineData(1, 1, 0, 0, 0)]
    [InlineData(1, 0, 1, 0, 0)]
    [InlineData(1, 0, 0, 1, 0)]
    [InlineData(1, 0, 0, 0, 16777217)]
    public async Task RejectsInvalidOrOversizedDockerHeaders(byte stream, byte reserved1, byte reserved2, byte reserved3, uint length)
    {
        var header = new byte[] { stream, reserved1, reserved2, reserved3, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), length);
        using var client = Client(header, false);
        await using var logs = await DockerLogStream.OpenAsync(client, new("safe-container", false), default);
        await Assert.ThrowsAsync<IOException>(() => logs.ReadAsync(new byte[65536], default).AsTask());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(9)]
    public async Task TruncatedHeaderOrPayloadIsNotMistakenForCleanEof(int length)
    {
        using var client = Client(Frame(1, "payload")[..length], false);
        await using var logs = await DockerLogStream.OpenAsync(client, new("safe-container", false), default);
        await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(logs));
    }

    [Fact]
    public async Task ExcessiveEmptyFramesAreRejectedWithoutSpinning()
    {
        using var client = Client(Enumerable.Range(0, 65).SelectMany(_ => Frame(1, "")).ToArray(), false);
        await using var logs = await DockerLogStream.OpenAsync(client, new("safe-container", false), default);
        await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(logs));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "application/vnd.docker.raw-stream")]
    [InlineData(HttpStatusCode.OK, "application/json")]
    public async Task HttpErrorsAndUnexpectedContentTypesAreRejectedAndDisposed(HttpStatusCode status, string contentType)
    {
        var content = new TrackingContent();
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var client = new HttpClient(new Handler(_ => new(status) { Content = content })) { BaseAddress = new("http://docker/") };
        await Assert.ThrowsAsync<IOException>(() => DockerLogStream.OpenAsync(client, new("safe-container", false), default));
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task InputBufferSizeIsBoundedAndCancellationIsHonored()
    {
        using var client = Client(Frame(1, "x"), false);
        await using var logs = await DockerLogStream.OpenAsync(client, new("safe-container", false), default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => logs.ReadAsync(Memory<byte>.Empty, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => logs.ReadAsync(new byte[65537], default).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => logs.ReadAsync(new byte[64], cancelled.Token).AsTask());
    }

    [Theory]
    [InlineData("../host")]
    [InlineData("foo?path=host")]
    public async Task CannotRequestHostPaths(string id)
    {
        using var client = Client([], false);
        await Assert.ThrowsAsync<IOException>(() => DockerLogStream.OpenAsync(client, new(id, false), default));
    }

    internal static byte[] Frame(byte stream, string text) => Frame(stream, Encoding.UTF8.GetBytes(text));
    internal static byte[] Frame(byte stream, byte[] payload)
    {
        var frame = new byte[payload.Length + 8]; frame[0] = stream;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8); return frame;
    }
    internal static async Task<string> ReadAllAsync(DockerLogStream logs) => Encoding.UTF8.GetString(await ReadAllBytesAsync(logs));
    private static async Task<byte[]> ReadAllBytesAsync(DockerLogStream logs)
    {
        using var output = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await logs.ReadAsync(buffer, default)) != 0) await output.WriteAsync(buffer.AsMemory(0, count));
        return output.ToArray();
    }
    private static HttpClient Client(byte[] payload, bool tty, int fragment = 2) =>
        new(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/containers/safe-container/logs?follow=1&stdout=1&stderr=1&timestamps=1&tail=200", request.RequestUri!.PathAndQuery);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedStream(payload, fragment)) };
            response.Content.Headers.ContentType = new(tty ? "application/vnd.docker.raw-stream" : "application/vnd.docker.multiplexed-stream");
            return response;
        })) { BaseAddress = new("http://docker/") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class FragmentedStream(byte[] bytes, int fragment) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(fragment, buffer.Length)], cancellationToken);
    }
    private sealed class TrackingContent : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
