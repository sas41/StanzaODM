using System.Diagnostics;
using System.Text.Json;
using Stanza.Tests.Web;

namespace Stanza.Tests.Build;

public sealed class DockerComposeFactAttribute : FactAttribute
{
    public DockerComposeFactAttribute()
    {
        // Rendering needs the CLI plugin, not a running daemon. Keep this separate from runtime opt-ins.
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker")
            {
                ArgumentList = { "compose", "version", "--short" }, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); Skip = "Docker Compose CLI is unavailable."; }
            else if (process.ExitCode != 0) Skip = "Docker Compose CLI is unavailable.";
        }
        catch (System.ComponentModel.Win32Exception) { Skip = "Docker Compose CLI is unavailable."; }
    }
}

public sealed class ComposeConfigurationTests
{
    [DockerComposeFact]
    public async Task Control_plane_manifest_renders_database_persistence_and_only_control_plane_services()
    {
        using var rendered = await RenderAsync("compose.control.yaml");
        var services = rendered.RootElement.GetProperty("services");
        Assert.Equal(new[] { "control-plane", "database", "initialize" }, services.EnumerateObject().Select(x => x.Name).Order());
        var control = services.GetProperty("control-plane");
        Assert.Equal("ghcr.io/sas41/stanzaodm-control-plane:2026-10-23-115011", control.GetProperty("image").GetString());
        Assert.False(control.TryGetProperty("build", out _));
        Assert.Equal("service_healthy", control.GetProperty("depends_on").GetProperty("database").GetProperty("condition").GetString());
        Assert.Equal("127.0.0.1", Assert.Single(control.GetProperty("ports").EnumerateArray()).GetProperty("host_ip").GetString());
        Assert.True(rendered.RootElement.GetProperty("volumes").TryGetProperty("control-plane-data", out _));
        Assert.True(rendered.RootElement.GetProperty("volumes").TryGetProperty("database-data", out _));
        Assert.True(services.GetProperty("database").TryGetProperty("healthcheck", out _));
    }

    [DockerComposeFact]
    public async Task Ordinary_agent_has_outbound_only_management_and_no_host_namespace_privileges()
    {
        using var rendered = await RenderAsync("compose.agent.yaml");
        var services = rendered.RootElement.GetProperty("services");
        Assert.Equal(new[] { "agent", "initialize-agent" }, services.EnumerateObject().Select(x => x.Name).Order());
        var agent = services.GetProperty("agent");
        Assert.Equal("ghcr.io/sas41/stanzaodm-agent:2026-10-23-115011", agent.GetProperty("image").GetString());
        Assert.False(agent.TryGetProperty("build", out _));
        Assert.False(agent.TryGetProperty("ports", out _)); Assert.False(agent.TryGetProperty("pid", out _));
        Assert.False(agent.TryGetProperty("cap_add", out _)); Assert.False(agent.TryGetProperty("user", out _));
        Assert.False(agent.GetProperty("environment").TryGetProperty("Agent__HostExecution__Enabled", out _));
        Assert.Equal("https://stanza.example.invalid", agent.GetProperty("environment").GetProperty("Agent__ControlPlaneUrl").GetString());
        AssertAgentMounts(agent);
    }

    [DockerComposeFact]
    public async Task Privileged_agent_override_merges_without_creating_a_second_agent_or_control_plane()
    {
        using var rendered = await RenderAsync("compose.agent.yaml", "compose.agent.privileged.yaml");
        var services = rendered.RootElement.GetProperty("services");
        Assert.Equal(new[] { "agent", "initialize-agent" }, services.EnumerateObject().Select(x => x.Name).Order());
        var agent = services.GetProperty("agent");
        Assert.Equal("ghcr.io/sas41/stanzaodm-agent:2026-10-23-115011", agent.GetProperty("image").GetString());
        Assert.Equal("0:0", agent.GetProperty("user").GetString());
        Assert.Equal("host", agent.GetProperty("pid").GetString());
        Assert.Equal("true", agent.GetProperty("environment").GetProperty("Agent__HostExecution__Enabled").GetString());
        Assert.Contains(agent.GetProperty("cap_add").EnumerateArray(), x => x.GetString() == "SYS_ADMIN");
        Assert.Contains(agent.GetProperty("cap_add").EnumerateArray(), x => x.GetString() == "SYS_CHROOT");
        Assert.False(agent.TryGetProperty("ports", out _));
        AssertAgentMounts(agent);
    }

    private static void AssertAgentMounts(JsonElement agent)
    {
        var mounts = agent.GetProperty("volumes").EnumerateArray().ToArray();
        Assert.Contains(mounts, x => x.GetProperty("source").GetString() == "/var/run/docker.sock" && x.GetProperty("target").GetString() == "/var/run/docker.sock");
        Assert.Contains(mounts, x => x.GetProperty("source").GetString() == "/var/lib/stanza-agent" && x.GetProperty("target").GetString() == "/var/lib/stanza-agent");
    }

    private static async Task<JsonDocument> RenderAsync(params string[] manifests)
    {
        var start = new ProcessStartInfo("docker") { WorkingDirectory = ArchitectureTests.RepositoryRoot(),
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("compose");
        foreach (var manifest in manifests) { start.ArgumentList.Add("-f"); start.ArgumentList.Add(manifest); }
        start.ArgumentList.Add("config"); start.ArgumentList.Add("--format"); start.ArgumentList.Add("json");
        start.Environment["STANZA_CONTROL_PLANE_URL"] = "https://stanza.example.invalid";
        start.Environment["STANZA_ENROLLMENT_TOKEN"] = "validation-only-token-not-an-enrollment-credential";
        start.Environment["STANZA_DOCKER_GID"] = "999";
        start.Environment["STANZA_AGENT_DATA_ROOT"] = "/var/lib/stanza-agent";
        start.Environment["STANZA_BIND_ADDRESS"] = "127.0.0.1";
        start.Environment["STANZA_HTTP_PORT"] = "8080";
        start.Environment["STANZA_VERSION"] = "2026-10-23-115011";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var output = await stdout; var error = await stderr;
        Assert.True(process.ExitCode == 0, error);
        return JsonDocument.Parse(output);
    }
}
