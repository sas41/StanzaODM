using System.Diagnostics;
using Stanza.Tests.Web;

namespace Stanza.Tests.Build;

public sealed class GitIgnoreTests(IgnoreRepository repository) : IClassFixture<IgnoreRepository>
{
    [Theory]
    [InlineData("src/Stanza.Core/bin/Debug/Stanza.Core.dll", true)]
    [InlineData("src/Stanza.ControlPlane/obj/project.assets.json", true)]
    [InlineData("tests/Stanza.Tests/TestResults/result.trx", true)]
    [InlineData("artifacts/control-plane/Stanza.ControlPlane.dll", true)]
    [InlineData(".env", true)]
    [InlineData(".env.production", true)]
    [InlineData("local/agent.env", true)]
    [InlineData("local/agent.env.production", true)]
    [InlineData("src/Stanza.ControlPlane/appsettings.Local.json", true)]
    [InlineData("src/Stanza.ControlPlane/appsettings.Development.local.json", true)]
    [InlineData("secrets/registry-password", true)]
    [InlineData("certificates/server.pfx", true)]
    [InlineData("certificates/agent.key", true)]
    [InlineData("custom/keys/key-123.xml", true)]
    [InlineData("data/control-plane/keys/private.xml", true)]
    [InlineData("agent_data/identity.pk8", true)]
    [InlineData("instances/agent-one/agent_data/configuration-cache/agent.env", true)]
    [InlineData("src/Stanza.ControlPlane/data/control-plane/keys/private.xml", true)]
    [InlineData("compose.override.yaml", true)]
    [InlineData("compose.production.override.yml", true)]
    [InlineData("local.sqlite3-wal", true)]
    [InlineData("logs/control-plane.log", true)]
    [InlineData(".vs/settings.json", true)]
    [InlineData(".env.example", false)]
    [InlineData(".env.production.example", false)]
    [InlineData("agent.env.example", false)]
    [InlineData("example.env", false)]
    [InlineData("compose.yaml", false)]
    [InlineData("compose.agent.yaml", false)]
    [InlineData("src/Stanza.Core/Stanza.Core.csproj", false)]
    [InlineData("src/Stanza.ControlPlane/Views/Home/Index.cshtml", false)]
    [InlineData("src/Stanza.ControlPlane/wwwroot/vendor/htmx.min.js", false)]
    [InlineData("src/Stanza.Agent/Dockerfile", false)]
    [InlineData(".github/workflows/docker-images.yml", false)]
    public async Task IgnoreRulesExcludeLocalStateButPreserveSourceAndExamples(string path, bool ignored)
    {
        var exit = await repository.GitAsync("check-ignore", "--no-index", "--quiet", "--", path);
        Assert.Equal(ignored ? 0 : 1, exit);
    }
}

public sealed class IgnoreRepository : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Directory.Exists("/tmp/opencode") ? "/tmp/opencode" : Path.GetTempPath(), $"stanza-ignore-test-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(ArchitectureTests.RepositoryRoot(), ".gitignore"), Path.Combine(directory, ".gitignore"));
        Assert.Equal(0, await GitAsync("init", "--quiet"));
    }

    public async Task<int> GitAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Never let a developer's Git environment redirect this isolated fixture into their repository.
        foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR" }) info.Environment.Remove(key);
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, "no-global-config");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.excludesfile=");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        await Task.WhenAll(stdout, stderr);
        Assert.True(process.ExitCode is 0 or 1, await stderr);
        return process.ExitCode;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }
}
