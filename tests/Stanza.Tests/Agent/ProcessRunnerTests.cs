using System.Diagnostics;
using Stanza.Agent.Runtime;

namespace Stanza.Tests.Agent;

public sealed class ProcessRunnerTests
{
    private readonly ProcessRunner runner = new(TimeProvider.System);

    [Fact]
    public async Task SensitiveStandardInputIsPipedWithoutShellOrArguments()
    {
        using var directory = new AgentTestDirectory();
        var request = new ProcessRequest("/usr/bin/cat", directory.Path, [], new Dictionary<string, string>(),
            TimeSpan.FromSeconds(5), StandardInput: "private-stdin;$(not-shell)\n");
        var result = await runner.RunAsync(request);
        Assert.Equal(request.StandardInput, result.StandardOutput);
        Assert.DoesNotContain("private-stdin", request.ToString());
    }

    [Fact]
    public async Task StructuredArgumentsCannotExecuteShellSyntax()
    {
        using var directory = new AgentTestDirectory();
        const string text = "$(touch injected); \"quoted\" && other | command\n";
        var result = await runner.RunAsync(new("/usr/bin/printf", directory.Path, ["%s", text],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(5)));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(text, result.StandardOutput);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task EnvironmentIsClearedAndWorkingDirectoryIsExplicit()
    {
        using var directory = new AgentTestDirectory();
        var result = await runner.RunAsync(new("/usr/bin/env", directory.Path, [],
            new Dictionary<string, string> { ["ONLY_ALLOWED"] = "yes" }, TimeSpan.FromSeconds(5)));
        Assert.Equal("ONLY_ALLOWED=yes\n", result.StandardOutput);
        var pwd = await runner.RunAsync(new("/usr/bin/pwd", directory.Path, [],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(5)));
        Assert.Equal(directory.Path, pwd.StandardOutput.Trim());
    }

    [Fact]
    public async Task VerboseOutputIsStreamedCompletelyWithoutTerminatingProcess()
    {
        using var directory = new AgentTestDirectory();
        var output = new System.Text.StringBuilder();
        var result = await runner.RunAsync(new("/usr/bin/printf", directory.Path, ["%s", new string('x', 70000)],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(5), OnOutput: (stream, text) => { output.Append(text); return Task.CompletedTask; }, CaptureOutput: false));
        Assert.False(result.OutputLimitExceeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(70000, output.Length);
        Assert.Empty(result.StandardOutput);
        Assert.DoesNotContain(new string('x', 64), result.ToString());
    }

    [Fact]
    public async Task TimeoutTerminatesActualProcess()
    {
        using var directory = new AgentTestDirectory();
        var stopwatch = Stopwatch.StartNew();
        var result = await runner.RunAsync(new("/usr/bin/sleep", directory.Path, ["20"],
            new Dictionary<string, string>(), TimeSpan.FromMilliseconds(100)));
        Assert.True(result.TimedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationTerminatesActualProcessAndPropagates()
    {
        using var directory = new AgentTestDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new("/usr/bin/sleep", directory.Path,
            ["20"], new Dictionary<string, string>(), TimeSpan.FromSeconds(30)), cancellation.Token));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StartFailureAndInvocationDiagnosticsAreRedacted()
    {
        using var directory = new AgentTestDirectory();
        var request = new ProcessRequest(Path.Combine(directory.Path, "secret-executable"), directory.Path,
            ["secret-argument"], new Dictionary<string, string> { ["SECRET"] = "secret-value" }, TimeSpan.FromSeconds(1));
        var exception = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(request));
        Assert.DoesNotContain("secret", exception.ToString());
        Assert.DoesNotContain("secret", request.ToString());
    }

    [Theory]
    [InlineData("relative/docker", "/tmp/opencode", 5)]
    [InlineData("/usr/bin/docker", "relative", 5)]
    [InlineData("/usr/bin/docker", "/tmp/opencode", 0)]
    [InlineData("/usr/bin/docker", "/tmp/opencode", 604801)]
    public async Task InvalidProcessLimitsAreRejected(string executable, string directory, int seconds) =>
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new(executable, directory, [],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(seconds))));
}
