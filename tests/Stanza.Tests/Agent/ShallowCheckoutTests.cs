using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class ShallowCheckoutTests
{
    [Fact]
    public async Task ActualGitCheckoutContainsOnlySelectedTipWithoutOtherBranchesTagsOrSubmoduleContents()
    {
        using var root = new AgentTestDirectory();
        var source = Path.Combine(root.Path, "source"); Directory.CreateDirectory(source);
        var processes = new ProcessRunner(TimeProvider.System);
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin:/bin", ["HOME"] = root.Path, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_AUTHOR_NAME"] = "Fixture", ["GIT_AUTHOR_EMAIL"] = "fixture@example.test",
            ["GIT_COMMITTER_NAME"] = "Fixture", ["GIT_COMMITTER_EMAIL"] = "fixture@example.test"
        };
        async Task<string> Git(string directory, params string[] arguments)
        {
            var result = await processes.RunAsync(new("/usr/bin/git", directory, arguments, environment, TimeSpan.FromSeconds(15)));
            Assert.Equal(0, result.ExitCode); return result.StandardOutput.Trim();
        }
        await Git(source, "init", "--quiet", "--initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(source, "app.txt"), "previous revision");
        await Git(source, "add", "."); await Git(source, "commit", "--quiet", "-m", "previous");
        var previous = await Git(source, "rev-parse", "HEAD");
        await Git(source, "tag", "old-tag");
        await File.WriteAllTextAsync(Path.Combine(source, "app.txt"), "selected tip");
        await File.WriteAllTextAsync(Path.Combine(source, ".gitmodules"), "[submodule \"dependency\"]\npath = dependency\nurl = https://unavailable.example.test/dependency\n");
        await Git(source, "add", ".");
        await Git(source, "update-index", "--add", "--cacheinfo", "160000," + previous + ",dependency");
        await Git(source, "commit", "--quiet", "-m", "selected");
        var tip = await Git(source, "rev-parse", "HEAD");
        await Git(source, "checkout", "--quiet", "-b", "unrelated");
        await File.WriteAllTextAsync(Path.Combine(source, "unrelated.txt"), "not selected");
        await Git(source, "add", "."); await Git(source, "commit", "--quiet", "-m", "unrelated");
        var runner = new LocalGitRunner(processes, new Uri(source).AbsoluteUri);
        var request = AgentWorkExecutorTests.Request() with { Sources = [new("https://fixture.example.test/source", "main", "checkout")] };
        var result = await AgentWorkExecutorTests.Executor(Path.Combine(root.Path, "agent"), runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code); Assert.Equal(tip, result.ResolvedSources!["checkout"]);
        var checkout = Path.Combine(root.Path, "agent", "operation-workspaces", request.Id.ToString("N"), "checkout");
        Assert.Equal("selected tip", await File.ReadAllTextAsync(Path.Combine(checkout, "app.txt")));
        Assert.Equal("1", await Git(checkout, "rev-list", "--all", "--count"));
        Assert.Equal("true", await Git(checkout, "rev-parse", "--is-shallow-repository"));
        Assert.Empty(await Git(checkout, "tag", "--list"));
        Assert.DoesNotContain("unrelated", await Git(checkout, "for-each-ref", "--format=%(refname)"));
        Assert.False(File.Exists(Path.Combine(checkout, "unrelated.txt")));
        Assert.True(File.Exists(Path.Combine(checkout, ".gitmodules")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(checkout, "dependency")));
    }

    private sealed class LocalGitRunner(IProcessRunner processes, string localUrl) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Executable != "/usr/bin/git") return Task.FromResult(new ProcessResult(0, "{\"services\":{}}", "", false, false));
            // Only the fixture swaps HTTPS transport for an isolated local repository. Production
            // protocol restrictions remain unchanged; Git still performs a real shallow fetch.
            var arguments = request.Arguments.Select(a => a == "https://fixture.example.test/source" ? localUrl : a).ToArray();
            return processes.RunAsync(request with { Arguments = ["-c", "protocol.file.allow=always", .. arguments] }, cancellationToken);
        }
    }
}
