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
    [InlineData("push", "refs/tags/2026-10-23-115011", "main", "true", "false")]
    [InlineData("push", "refs/tags/2024-02-29-235959", "main", "true", "false")]
    [InlineData("push", "refs/tags/2026-01-01-000000", "main", "true", "false")]
    [InlineData("push", "refs/tags/2026-02-29-115011", "main", "false", "false")]
    [InlineData("push", "refs/tags/2026-02-30-115011", "main", "false", "false")]
    [InlineData("push", "refs/tags/2026-13-01-115011", "main", "false", "false")]
    [InlineData("push", "refs/tags/2026-10-23-240000", "main", "false", "false")]
    [InlineData("push", "refs/tags/2026-10-23-116000", "main", "false", "false")]
    [InlineData("push", "refs/tags/2026-10-23-115060", "main", "false", "false")]
    [InlineData("push", "refs/tags/2026-10-23-1150", "main", "false", "false")]
    [InlineData("push", "refs/tags/2026-10-23-115011-extra", "main", "false", "false")]
    [InlineData("push", "refs/tags/v2026-10-23-115011", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3-rc.1", "main", "false", "false")]
    [InlineData("push", "refs/tags/vfoo", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2", "main", "false", "false")]
    [InlineData("push", "refs/tags/v01.2.3", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.02.3", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.03", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3-01", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3-rc.01", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3-", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3-rc..1", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3+", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3+build..1", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3/path", "main", "false", "false")]
    [InlineData("push", "refs/tags/v1.2.3\npublish=true", "main", "false", "false")]
    [InlineData("push", "refs/tags/unrelated", "main", "false", "false")]
    [InlineData("pull_request", "refs/pull/1/merge", "main", "false", "false")]
    [InlineData("pull_request", "refs/heads/main", "main", "false", "false")]
    [InlineData("pull_request_target", "refs/heads/main", "main", "false", "false")]
    [InlineData("workflow_dispatch", "refs/heads/main", "main", "false", "false")]
    [InlineData("workflow_dispatch", "refs/tags/2026-10-23-115011", "main", "false", "false")]
    [InlineData("pull_request", "refs/tags/2026-10-23-115011", "main", "false", "false")]
    [InlineData("push", "refs/heads/main; echo injection", "main", "false", "false")]
    public async Task PublishingIsLimitedToDefaultBranchAndValidUtcDateTagPushes(string eventName, string reference,
        string defaultBranch, string publish, string latest)
    {
        var (exit, values) = await RunPolicyAsync("ExampleOwner/StanzaODM", "agent", eventName, reference, defaultBranch);
        Assert.Equal(0, exit);
        Assert.Equal("ghcr.io/exampleowner/stanzaodm-agent", values["name"]);
        Assert.Equal(publish, values["publish"]);
        Assert.Equal(latest, values["latest"]);
        Assert.Equal(publish == "true" && reference.StartsWith("refs/tags/", StringComparison.Ordinal)
            ? reference["refs/tags/".Length..] : "2026-10-23-115011", values["version"]);
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

    [Fact]
    public async Task Both_components_use_exactly_the_same_workflow_version_and_the_expected_GHCR_repository()
    {
        foreach (var component in new[] { "control-plane", "agent" })
        {
            var (exit, values) = await RunPolicyAsync("sas41/StanzaODM", component, "push", "refs/heads/main", "main", "2026-12-31-235959");
            Assert.Equal(0, exit); Assert.Equal("2026-12-31-235959", values["version"]);
            Assert.Equal($"ghcr.io/sas41/stanzaodm-{component}", values["name"]);
        }
    }

    [Theory]
    [InlineData("2026-02-30-115011")]
    [InlineData("2026-10-23-115011\npublish=true")]
    [InlineData("v1.2.3")]
    public async Task Invalid_shared_build_version_cannot_emit_workflow_outputs(string version)
    {
        var (exit, values) = await RunPolicyAsync("sas41/StanzaODM", "agent", "push", "refs/heads/main", "main", version);
        Assert.NotEqual(0, exit); Assert.Empty(values);
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
    public void WorkflowBuildsBothImagesWithSharedReleaseVersionAndGuardsRegistryCredentials()
    {
        var root = ArchitectureTests.RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/docker-images.yml"));
        Assert.Contains("needs: release", workflow);
        Assert.Contains("dotnet test Stanza.slnx --configuration Release", workflow);
        Assert.Contains("node --test tests/Stanza.Tests/*/*.test.cjs", workflow);
        Assert.DoesNotContain("pull_request_target:", workflow);
        Assert.Contains("permissions:\n  contents: read", workflow);
        Assert.Contains("packages: write", workflow);
        Assert.Contains("if: steps.image.outputs.publish == 'true'", workflow);
        Assert.Contains("push: ${{ steps.image.outputs.publish == 'true' }}", workflow);
        Assert.Contains("- name: Log in to GHCR for publishing only\n        if: steps.image.outputs.publish == 'true'", workflow);
        Assert.Contains("password: ${{ secrets.GITHUB_TOKEN }}", workflow);
        Assert.Contains("bash .github/scripts/image-metadata.sh", workflow);
        Assert.Contains("cache-to: type=gha,mode=max,scope=${{ matrix.component }}", workflow);
        Assert.Contains("docker compose -f compose.control.yaml config --quiet", workflow);
        Assert.Contains("docker compose -f compose.agent.yaml config --quiet", workflow);
        Assert.Contains("docker compose -f compose.agent.yaml -f compose.agent.privileged.yaml config --quiet", workflow);
        Assert.Contains("platforms: linux/amd64", workflow);
        Assert.Contains("flavor: latest=false", workflow);
        Assert.Contains("type=raw,value=latest,enable=${{ steps.image.outputs.latest == 'true' }}", workflow);
        Assert.DoesNotContain("type=semver", workflow);
        Assert.Contains("BUILD_VERSION: ${{ needs.release.outputs.version }}", workflow);
        Assert.Contains("version: ${{ steps.release.outputs.version }}", workflow);
        Assert.Contains("type=raw,value=${{ steps.image.outputs.version }},priority=1000", workflow);
        Assert.Contains("org.opencontainers.image.version=${{ needs.release.outputs.version }}", workflow);
        Assert.Contains("tags: [\"[0-9][0-9][0-9][0-9]-*\"]", workflow);
        Assert.Contains("type=sha,prefix=sha-", workflow);
        Assert.Contains("rhysd/actionlint:1.7.7@sha256:", workflow);
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

    [Fact]
    public void Client_test_glob_in_workflow_discovers_every_existing_client_suite()
    {
        var tests = Path.Combine(ArchitectureTests.RepositoryRoot(), "tests/Stanza.Tests");
        var clients = Directory.GetFiles(tests, "*.test.cjs", SearchOption.AllDirectories);
        Assert.NotEmpty(clients);
        Assert.All(clients, client => Assert.Equal(2, Path.GetRelativePath(tests, client).Split(Path.DirectorySeparatorChar).Length));
        foreach (var directory in new[] { "Live", "Editing", "Sources", "Logs" })
            Assert.Contains(clients, client => Path.GetDirectoryName(client) == Path.Combine(tests, directory));
    }

    [Fact]
    public void Root_contains_exactly_the_requested_compose_entrypoints_with_privilege_opt_in()
    {
        var root = ArchitectureTests.RepositoryRoot();
        var manifests = Directory.GetFiles(root).Where(path => Regex.IsMatch(Path.GetFileName(path), @"^(?:docker-)?compose(?:\..+)?\.ya?ml$"))
            .Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "compose.agent.privileged.yaml", "compose.agent.yaml", "compose.control.yaml" }, manifests);
        var control = File.ReadAllText(Path.Combine(root, "compose.control.yaml"));
        var ordinary = File.ReadAllText(Path.Combine(root, "compose.agent.yaml"));
        var privileged = File.ReadAllText(Path.Combine(root, "compose.agent.privileged.yaml"));
        Assert.Contains("docker compose -f compose.control.yaml up -d", control);
        Assert.Contains("docker compose -f compose.agent.yaml -f compose.agent.privileged.yaml up -d", privileged);
        Assert.Contains("image: ghcr.io/sas41/stanzaodm-control-plane:${STANZA_VERSION:-latest}", control);
        Assert.Contains("image: ghcr.io/sas41/stanzaodm-agent:${STANZA_VERSION:-latest}", ordinary);
        Assert.DoesNotContain("build:", control); Assert.DoesNotContain("build:", ordinary);
        Assert.Contains("pull_policy: always", control); Assert.Contains("pull_policy: always", ordinary);
        Assert.DoesNotContain("${STANZA_AGENT_DATA_ROOT", ordinary);
        Assert.Contains("- ./agent_data:/agent-data", ordinary);
        Assert.Contains("- ./agent_data:/var/lib/stanza-agent", ordinary);
        Assert.Contains("Agent__PersistentDataRoot: /var/lib/stanza-agent", ordinary);
        Assert.DoesNotContain("\nname: stanza-agent", ordinary);
        Assert.DoesNotContain("container_name:", ordinary);
        Assert.DoesNotContain("pid: host", ordinary); Assert.DoesNotContain("SYS_ADMIN", ordinary);
        Assert.DoesNotContain("Agent__HostExecution__Enabled", ordinary);
        Assert.Contains("pid: host", privileged); Assert.Contains("SYS_ADMIN", privileged);
        Assert.Contains("Agent__HostExecution__Enabled: \"true\"", privileged);
    }

    private static async Task<(int Exit, Dictionary<string, string> Values)> RunPolicyAsync(string repository,
        string component, string eventName, string reference, string defaultBranch, string buildVersion = "2026-10-23-115011")
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
            info.Environment["BUILD_VERSION"] = buildVersion;
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
