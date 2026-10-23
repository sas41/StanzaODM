using System.Collections.Concurrent;
using System.Text;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class OutputStreamingTests
{
    [Fact]
    public async Task ExecutorRetainsVerboseFailureDiagnosticsWithoutLeakingInternalConfigurationOrCredentials()
    {
        using var directory = new AgentTestDirectory();
        var runner = new AgentWorkExecutorTests.FakeRunner
        {
            Result = request => request.Arguments.Contains("config")
                ? new(0, "{\"services\":{\"app\":{\"image\":\"busybox:latest\",\"environment\":{\"PRIVATE\":\"internal-secret\"}}}}", "", false, false)
                : new(17, new string('x', 70000), "manifest rejected: split-password\n", false, false)
        };
        var executor = AgentWorkExecutorTests.Executor(directory.Path, runner);
        var chunks = new List<AgentOutputChunk>();
        executor.Output += (_, chunk) => { chunks.Add(chunk); return Task.CompletedTask; };
        var response = await executor.ExecuteAsync(AgentWorkExecutorTests.Request("pull") with
        {
            Policy = new(TimeoutSeconds: 0), Registries = [new("registry.example.test", "user", "split-password")]
        });
        Assert.False(response.Succeeded); Assert.Equal("runtime_failed", response.Code);
        var output = string.Concat(chunks.Select(x => x.Text));
        Assert.True(output.Length > 70000);
        Assert.Contains("manifest rejected: [REDACTED]", string.Concat(chunks.Where(x => x.Stream == "stderr").Select(x => x.Text)));
        Assert.Contains("process exit code 17", output);
        Assert.DoesNotContain("split-password", output); Assert.DoesNotContain("internal-secret", output);
        Assert.Equal(Enumerable.Range(1, chunks.Count).Select(x => (long)x), chunks.Select(x => x.Sequence));
        Assert.All(runner.Requests, x => Assert.Equal(Timeout.InfiniteTimeSpan, x.Timeout));
        Assert.False(runner.Requests.Single(x => x.Arguments.Contains("pull")).CaptureOutput);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    public void CredentialRedactionHandlesEveryChunkBoundary(int chunkSize)
    {
        var redactor = new OutputRedactor(["secret-password", "secret"]);
        const string original = "before secret-password and secret after";
        var result = new StringBuilder();
        for (var i = 0; i < original.Length; i += chunkSize) result.Append(redactor.Append(original.Substring(i, Math.Min(chunkSize, original.Length - i))));
        result.Append(redactor.Flush());
        Assert.Equal("before [REDACTED] and [REDACTED] after", result.ToString());
    }

    [Fact]
    public async Task StdoutAndFailureStderrArriveBeforeTheProcessExitsAndUnlimitedTimeoutIsCancellable()
    {
        using var directory = new AgentTestDirectory();
        var chunks = new ConcurrentQueue<(string Stream, string Text)>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ProcessRequest("/bin/sh", directory.Path, ["-c", "printf 'started'; printf 'failure detail' >&2; sleep 20"],
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" }, Timeout.InfiniteTimeSpan,
            OnOutput: (stream, text) => { chunks.Enqueue((stream, text)); if (chunks.Count >= 2) received.TrySetResult(); return Task.CompletedTask; }, CaptureOutput: false);
        using var cancel = new CancellationTokenSource();
        var run = new ProcessRunner(TimeProvider.System).RunAsync(request, cancel.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        Assert.Contains(chunks, x => x.Stream == "stdout" && x.Text.Contains("started"));
        Assert.Contains(chunks, x => x.Stream == "stderr" && x.Text.Contains("failure detail"));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task OutputPersistenceFailureTerminatesInsteadOfDeadlockingAVerboseProcess()
    {
        using var directory = new AgentTestDirectory();
        var run = new ProcessRunner(TimeProvider.System).RunAsync(new("/usr/bin/yes", directory.Path, [], new Dictionary<string, string>(),
            Timeout.InfiniteTimeSpan, OnOutput: (_, _) => throw new IOException("recording unavailable"), CaptureOutput: false));
        var exception = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("recording unavailable", exception.Message);
    }
}
