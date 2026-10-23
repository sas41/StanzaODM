using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Enrollment;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class EnrollmentClientTests
{
    [Theory]
    [InlineData("http://192.0.2.10:8888", false, false)]
    [InlineData("http://192.0.2.10:8888", true, true)]
    [InlineData("https://control.example", false, true)]
    [InlineData("https://control.example", true, true)]
    [InlineData("http://secret@control.example", true, false)]
    [InlineData("http://control.example/path", true, false)]
    [InlineData("http://control.example?token=secret", true, false)]
    public async Task Transport_is_checked_before_any_enrollment_credential_is_sent(string origin, bool enabled, bool send)
    {
        var sent = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            sent++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            { Content = new StringContent(JsonSerializer.Serialize(new AgentEnrollmentResponse(1, "pending"), JsonSerializerOptions.Web)) });
        }));
        var client = new EnrollmentClient(http, options: Options.Create(new AgentOptions { AllowInsecureHttp = enabled }));
        Assert.Equal(send ? EnrollmentStatus.Pending : EnrollmentStatus.Unavailable, await client.RegisterAsync(new(origin), Request(), default));
        Assert.Equal(send ? 1 : 0, sent);
    }

    [Theory]
    [InlineData("pending", EnrollmentStatus.Pending)]
    [InlineData("approved", EnrollmentStatus.Approved)]
    [InlineData("rejected", EnrollmentStatus.Rejected)]
    [InlineData("revoked", EnrollmentStatus.Revoked)]
    [InlineData("unknown", EnrollmentStatus.Incompatible)]
    public async Task SendsVersionedProofAndHandlesApprovalWithoutEnablingWork(string status, EnrollmentStatus expected)
    {
        using var directory = new AgentTestDirectory();
        using var identity = AgentIdentity.LoadOrCreate(directory.Path);
        var request = identity.CreateEnrollmentRequest("secret-enrollment-token", "test-host", DateTimeOffset.UtcNow);
        var handler = new Handler(async (message, cancellation) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://control.example/api/agent-enrollment", message.RequestUri!.AbsoluteUri);
            Assert.Null(message.Headers.Authorization);
            Assert.DoesNotContain(request.Token, message.RequestUri.AbsoluteUri);
            var content = await message.Content!.ReadAsStringAsync(cancellation);
            var received = JsonSerializer.Deserialize<AgentEnrollmentRequest>(content, JsonSerializerOptions.Web)!;
            Assert.Equal(request, received);
            Assert.True(AgentEnrollmentProtocol.VerifyProof(received, DateTimeOffset.UtcNow));
            return new(HttpStatusCode.Accepted)
            {
                Content = new StringContent(JsonSerializer.Serialize(new AgentEnrollmentResponse(1, status), JsonSerializerOptions.Web))
            };
        });
        using var http = new HttpClient(handler);
        var result = await new EnrollmentClient(http).RegisterAsync(new Uri("https://control.example"), request, default);
        Assert.Equal(expected, result);
        var state = new AgentState();
        state.Set(new(true, result, identity.AgentId));
        Assert.True(state.Snapshot.Ready);
        Assert.False(state.Snapshot.CanExecuteWork);
        Assert.False(state.Snapshot.CommandChannelAvailable);
    }

    [Theory]
    [InlineData(401, EnrollmentStatus.Rejected)]
    [InlineData(403, EnrollmentStatus.Rejected)]
    [InlineData(409, EnrollmentStatus.Incompatible)]
    [InlineData(426, EnrollmentStatus.Incompatible)]
    [InlineData(429, EnrollmentStatus.Retrying)]
    [InlineData(500, EnrollmentStatus.Retrying)]
    [InlineData(503, EnrollmentStatus.Retrying)]
    [InlineData(302, EnrollmentStatus.Retrying)]
    public async Task MapsErrorsWithoutReadingOrReturningSecretResponse(int statusCode, EnrollmentStatus expected)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent("secret-token diagnostic")
        })));
        Assert.Equal(expected, await new EnrollmentClient(http).RegisterAsync(new("https://control.example"), Request(), default));
    }

    [Theory]
    [InlineData("not json", EnrollmentStatus.Retrying)]
    [InlineData("null", EnrollmentStatus.Retrying)]
    [InlineData("{}", EnrollmentStatus.Incompatible)]
    [InlineData("{\"protocolVersion\":2,\"status\":\"pending\"}", EnrollmentStatus.Incompatible)]
    public async Task HandlesMalformedOrIncompatibleResponse(string body, EnrollmentStatus expected)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        })));
        Assert.Equal(expected, await new EnrollmentClient(http).RegisterAsync(new("https://control.example"), Request(), default));
    }

    [Fact]
    public async Task ResponseBodyIsBounded()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('s', 100000))
        })));
        Assert.Equal(EnrollmentStatus.Retrying, await new EnrollmentClient(http).RegisterAsync(new("https://control.example"), Request(), default));
    }

    [Fact]
    public async Task NetworkOrTlsFailuresRetryWithoutCertificateBypass()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("secret token in untrusted server diagnostic")));
        Assert.Equal(EnrollmentStatus.Retrying, await new EnrollmentClient(http).RegisterAsync(new("https://control.example"), Request(), default));
    }

    [Fact]
    public async Task ShutdownCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EnrollmentClient(http)
            .RegisterAsync(new("https://control.example"), Request(), cancellation.Token));
    }

    [Fact]
    public async Task SlowResponseBodyIsBoundedByTimeoutWithControlledTime()
    {
        var clock = new EnrollmentWorkerTests.ManualTimeProvider();
        var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream(enteredRead))
        })));
        var registration = new EnrollmentClient(http, clock).RegisterAsync(new("https://control.example"), Request(), default);
        await enteredRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var timer = await clock.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), timer.DueTime);
        clock.Advance(timer);
        Assert.Equal(EnrollmentStatus.Retrying, await registration.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class WaitingStream(TaskCompletionSource enteredRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            enteredRead.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static AgentEnrollmentRequest Request() => new("secret-token", AgentEnrollmentProtocol.Name, 1, "id", "key", "hostname", 0, "nonce", "signature");
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
