using System.Diagnostics;
using System.Text.RegularExpressions;
using Stanza.Tests.Web;

namespace Stanza.Tests.Build;

public sealed class RepositoryConfigurationTests
{
    [Theory]
    [InlineData("push", "refs/heads/main", "main", "true", "true")]
    [InlineData("push", "refs/heads/trunk", "trunk", "true", "true")]
    [InlineData("push", "refs/heads/main", "trunk", "false", "false")]
    [InlineData("push", "refs/heads/feature", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3", "main", "true", "false")]
    [InlineData("push", "refs/tags/v1.2.3-rc.1", "main", "true", "false")]
    [InlineData("push", "refs/tags/unrelated", "main", "false", "false")]
    [InlineData("pull_request", "refs/pull/1/merge", "main", "false", "false")]
    [InlineData("pull_request", "refs/heads/main", "main", "false", "false")]
    [InlineData("pull_request_target", "refs/heads/main", "main", "false", "false")]
    [InlineData("workflow_dispatch", "refs/heads/main", "main", "false", "false")]
    [InlineData("push", "refs/heads/main; echo injection", "main", "false", "false")]
    public async Task PublishingIsLimitedToDefaultBranchAndVersionTagPushes(string eventName, string reference,
        string defaultBranch, string publish, string latest)
    {
        var (exit, values) = await RunPolicyAsync("ExampleOwner/StanzaODM", "agent", eventName, reference, defaultBranch);
        Assert.Equal(0, exit);
        Assert.Equal("ghcr.io/exampleowner/stanzaodm-agent", values["name"]);
        Assert.Equal(publish, values["publish"]);
        Assert.Equal(latest, values["latest"]);
    }

    [Theory]
    [InlineData("control-plane")]
    [InlineData("agent")]
    public async Task BothImageNamesUseLowercaseRepositoryAndDistinctSuffixes(string component)
    {
        var (exit, values) = await RunPolicyAsync("Owner/Repository", component, "push", "refs/heads/main", "main");
        Assert.Equal(0, exit);
        Assert.Equal($"ghcr.io/owner/repository-{component}", values["name"]);
    }

    [Theory]
    [InlineData("owner/repository", "other")]
    [InlineData("owner/repository\npublish=true", "agent")]
    [InlineData("owner/repository/extra", "agent")]
    [InlineData("owner/repository", "agent\nlatest=true")]
    public async Task MalformedMetadataCannotInjectWorkflowOutputs(string repository, string component)
    {
        var (exit, values) = await RunPolicyAsync(repository, component, "push", "refs/heads/main", "main");
        Assert.NotEqual(0, exit);
        Assert.Empty(values);
    }

    [Fact]
    public void WorkflowBuildsBothImagesAfterTestsAndGuardsRegistryCredentials()
    {
        var root = ArchitectureTests.RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/docker-images.yml"));
        Assert.Contains("needs: verify", workflow);
        Assert.Contains("dotnet test Stanza.slnx --configuration Release", workflow);
        Assert.Contains("node --test tests/Stanza.Tests/Live/status.test.cjs", workflow);
        Assert.DoesNotContain("pull_request_target:", workflow);
        Assert.Contains("permissions:\n  contents: read", workflow);
        Assert.Contains("packages: write", workflow);
        Assert.Contains("if: steps.image.outputs.publish == 'true'", workflow);
        Assert.Contains("push: ${{ steps.image.outputs.publish == 'true' }}", workflow);
        Assert.Contains("password: ${{ secrets.GITHUB_TOKEN }}", workflow);
        Assert.Contains("bash .github/scripts/image-metadata.sh", workflow);
        Assert.Contains("cache-to: type=gha,mode=max,scope=${{ matrix.component }}", workflow);
        Assert.DoesNotContain("secrets.", Regex.Replace(workflow, @"secrets\.GITHUB_TOKEN", "token"));
        foreach (var component in new[] { "control-plane", "agent" }) Assert.Contains($"component: {component}", workflow);
        foreach (var project in new[] { "Stanza.ControlPlane", "Stanza.Agent" })
        {
            var file = $"src/{project}/Dockerfile";
            Assert.Contains($"dockerfile: {file}", workflow);
            Assert.True(File.Exists(Path.Combine(root, file)));
        }
        var actions = Regex.Matches(workflow, @"uses:\s+[^@\s]+@([^\s]+)");
        Assert.NotEmpty(actions);
        Assert.All(actions.Cast<Match>(), action => Assert.Matches("^[a-f0-9]{40}$", action.Groups[1].Value));
    }

    private static async Task<(int Exit, Dictionary<string, string> Values)> RunPolicyAsync(string repository,
        string component, string eventName, string reference, string defaultBranch)
    {
        var root = Directory.Exists("/tmp/opencode") ? "/tmp/opencode" : Path.GetTempPath();
        var output = Path.Combine(root, $"stanza-image-policy-{Guid.NewGuid():N}");
        try
        {
            var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(Path.Combine(ArchitectureTests.RepositoryRoot(), ".github/scripts/image-metadata.sh"));
            info.Environment["GITHUB_REPOSITORY"] = repository;
            info.Environment["COMPONENT"] = component;
            info.Environment["GITHUB_EVENT_NAME"] = eventName;
            info.Environment["GITHUB_REF"] = reference;
            info.Environment["DEFAULT_BRANCH"] = defaultBranch;
            info.Environment["GITHUB_OUTPUT"] = output;
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            await Task.WhenAll(stdout, stderr);
            var values = File.Exists(output) ? File.ReadAllLines(output).ToDictionary(line => line.Split('=', 2)[0], line => line.Split('=', 2)[1]) : [];
            return (process.ExitCode, values);
        }
        finally { File.Delete(output); }
    }
}
