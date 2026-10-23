using System.Diagnostics;
using System.Text.RegularExpressions;
using Stanza.Agent.Runtime;
using Stanza.Tests.Agent;
using Stanza.Tests.Web;

namespace Stanza.Tests.Build;

public sealed class DeploymentGuideTests
{
    [Fact]
    public void Environment_template_documents_every_compose_variable_without_shipped_credentials()
    {
        var root = ArchitectureTests.RepositoryRoot();
        var template = File.ReadAllText(Path.Combine(root, "example.env"));
        var keys = Regex.Matches(template, @"(?m)^(?:#\s*)?([A-Z][A-Z0-9_]*)=").Select(x => x.Groups[1].Value).ToHashSet();
        foreach (var compose in Directory.GetFiles(root, "compose*.yaml"))
            foreach (Match variable in Regex.Matches(File.ReadAllText(compose), @"\$\{([A-Z][A-Z0-9_]*)"))
                Assert.Contains(variable.Groups[1].Value, keys);
        Assert.Contains("STANZA_ENROLLMENT_TOKEN=CHANGE_ME_", template);
        Assert.Contains("STANZA_DOCKER_GID=CHANGE_ME", template);
        Assert.Contains("STANZA_ALLOW_INSECURE_HTTP=false", template);
        Assert.Contains("STANZA_BIND_ADDRESS=127.0.0.1", template);
        Assert.Contains("STANZA_VERSION=latest", template);
        Assert.DoesNotContain("POSTGRES_PASSWORD=", template);
    }

    [DockerComposeFact]
    public async Task Template_can_render_the_complete_stack_before_bootstrap_without_executing_placeholder_credentials()
    {
        using var directory = new AgentTestDirectory(); var root = ArchitectureTests.RepositoryRoot();
        File.Copy(Path.Combine(root, "compose.yaml"), Path.Combine(directory.Path, "compose.yaml"));
        File.Copy(Path.Combine(root, "example.env"), Path.Combine(directory.Path, ".env"));
        var unset = Regex.Matches(File.ReadAllText(Path.Combine(root, "example.env")), @"(?m)^([A-Z][A-Z0-9_]*)=")
            .ToDictionary(x => x.Groups[1].Value, _ => (string?)null);
        using var rendered = await ComposeConfigurationTests.RenderEnvironmentAtAsync(directory.Path, null, null, unset, "compose.yaml");
        var services = rendered.RootElement.GetProperty("services");
        Assert.Equal("local-agent", services.GetProperty("agent").GetProperty("environment").GetProperty("Agent__Name").GetString());
        Assert.Equal("false", services.GetProperty("control-plane").GetProperty("environment").GetProperty("Stanza__AllowInsecureHttp").GetString());
        Assert.Equal("CHANGE_ME", Assert.Single(services.GetProperty("agent").GetProperty("group_add").EnumerateArray()).GetString());
    }

    [DockerComposeFact]
    public async Task Readme_smoke_compose_and_environment_render_and_generate_the_expected_HTML()
    {
        using var directory = new AgentTestDirectory();
        var readme = File.ReadAllText(Path.Combine(ArchitectureTests.RepositoryRoot(), "README.md"));
        var compose = Block(readme, "yaml", "test-web:");
        var environment = Block(readme, "dotenv", "TEST_MESSAGE=Environment variables work!");
        Assert.Contains("TEST_MESSAGE: ${TEST_MESSAGE}", compose);
        Assert.Contains("TEST_MESSAGE: $${TEST_MESSAGE}", compose);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "compose.yaml"), compose);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, ".env"), environment);
        using var rendered = await ComposeConfigurationTests.RenderEnvironmentAtAsync(directory.Path, null, null,
            new Dictionary<string, string?> { ["TEST_MESSAGE"] = null, ["TEST_ENV"] = null, ["TEST_VALUE"] = null }, "compose.yaml");
        var web = rendered.RootElement.GetProperty("services").GetProperty("test-web");
        Assert.Equal("nginx:alpine", web.GetProperty("image").GetString());
        Assert.Equal("33333", Assert.Single(web.GetProperty("ports").EnumerateArray()).GetProperty("published").GetString());
        var values = web.GetProperty("environment");
        Assert.Equal("Environment variables work!", values.GetProperty("TEST_MESSAGE").GetString());
        Assert.Equal("development", values.GetProperty("TEST_ENV").GetString());
        Assert.Equal("12345", values.GetProperty("TEST_VALUE").GetString());
        var html = Path.Combine(directory.Path, "index.html");
        var script = AgentHostPathOverrides.RenderedPath(web.GetProperty("command")[2].GetString()!)
            .Replace("/usr/share/nginx/html/index.html", html, StringComparison.Ordinal)
            .Replace("exec nginx -g 'daemon off;'", "true", StringComparison.Ordinal);
        var start = new ProcessStartInfo("/bin/sh") { WorkingDirectory = directory.Path, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(script);
        foreach (var value in values.EnumerateObject()) start.Environment[value.Name] = value.Value.GetString();
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        await output; Assert.True(process.ExitCode == 0, await errors);
        var content = await File.ReadAllTextAsync(html);
        Assert.Contains("<h1>Docker is working!</h1>", content);
        Assert.Contains("TEST_MESSAGE: Environment variables work!", content);
        Assert.Contains("TEST_ENV: development", content); Assert.Contains("TEST_VALUE: 12345", content);
    }

    [DockerComposeFact]
    public async Task Git_recipe_resolves_a_repository_Dockerfile_or_the_documented_managed_custom_file()
    {
        using var directory = new AgentTestDirectory();
        var readme = File.ReadAllText(Path.Combine(ArchitectureTests.RepositoryRoot(), "README.md"));
        var block = Assert.Single(Regex.Matches(readme, @"(?ms)^   ```yaml\n(?<body>.*?)^   ```\s*$")
            .Select(x => x.Groups["body"].Value), value => value.Contains("stanza-git-demo:local", StringComparison.Ordinal));
        var compose = string.Join('\n', block.Split('\n').Select(line => line.StartsWith("   ", StringComparison.Ordinal) ? line[3..] : line));
        var repository = Directory.CreateDirectory(Path.Combine(directory.Path, "sources/app")).FullName;
        await File.WriteAllTextAsync(Path.Combine(repository, "Dockerfile"), "FROM alpine:3.22\n");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "Dockerfile.app"), "FROM alpine:3.22\n");
        foreach (var custom in new[] { false, true })
        {
            var authored = custom ? compose.Replace("context: ./sources/app", "context: ./sources/app\n      dockerfile: ../../Dockerfile.app", StringComparison.Ordinal) : compose;
            await File.WriteAllTextAsync(Path.Combine(directory.Path, "compose.yaml"), authored);
            using var rendered = await ComposeConfigurationTests.RenderEnvironmentAtAsync(directory.Path, null, null, null, "compose.yaml");
            var build = rendered.RootElement.GetProperty("services").GetProperty("app").GetProperty("build");
            var context = build.GetProperty("context").GetString()!;
            Assert.Equal(repository, context);
            Assert.Equal(custom ? Path.Combine(directory.Path, "Dockerfile.app") : Path.Combine(repository, "Dockerfile"),
                Path.GetFullPath(build.GetProperty("dockerfile").GetString()!, context));
        }
    }

    [Fact]
    public void Guide_covers_both_Dockerfile_sources_and_does_not_claim_automatic_Git_compose_or_data_transfer()
    {
        var readme = File.ReadAllText(Path.Combine(ArchitectureTests.RepositoryRoot(), "README.md"));
        foreach (var heading in new[] { "### 1.", "### 2.", "### 3.", "### 4.", "### 5." }) Assert.Contains(heading, readme);
        Assert.Contains("dockerfile: ../../Dockerfile.app", readme);
        Assert.Contains("context: ./sources/app", readme);
        Assert.Contains("context: ./worker", readme);
        Assert.Contains("not a Git-managed Compose file", readme);
        Assert.Contains("not persistent workload data", readme);
        Assert.Contains("docker compose up -d control-plane", readme);
        Assert.Contains("./agent_data", readme);
    }

    private static string Block(string readme, string language, string content) =>
        Assert.Single(Regex.Matches(readme, @"(?ms)^```" + Regex.Escape(language) + @"\n(?<body>.*?)^```\s*$")
            .Select(x => x.Groups["body"].Value), block => block.Contains(content, StringComparison.Ordinal));
}
