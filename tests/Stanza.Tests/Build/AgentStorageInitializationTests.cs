using System.Diagnostics;
using System.Text.RegularExpressions;
using Stanza.Agent;
using Stanza.Tests.Web;

namespace Stanza.Tests.Build;

public sealed class LinuxInitializationFactAttribute : FactAttribute
{
    public LinuxInitializationFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "The packaged Agent initializer targets Linux.";
    }
}

public sealed class LinuxInitializationTheoryAttribute : TheoryAttribute
{
    public LinuxInitializationTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "The packaged Agent initializer targets Linux.";
    }
}

public sealed class AgentStorageInitializationTests
{
    private static readonly string[] Stores = ["request-ledger", "credentials", "runtime-home", "configuration-cache", "operation-workspaces"];

    [LinuxInitializationFact]
    public async Task Fresh_initialization_can_repeat_before_the_agent_creates_an_identity()
    {
        using var storage = new Initializer();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await storage.RunAsync();
            Assert.True(result.Exit == 0, result.Error);
            Assert.Equal(Stores.Order(), Directory.EnumerateDirectories(storage.Data).Select(Path.GetFileName).Order());
            Assert.False(File.Exists(Path.Combine(storage.Data, "identity.pk8")));
        }
    }

    [LinuxInitializationTheory]
    [InlineData("request-ledger")]
    [InlineData("credentials")]
    [InlineData("runtime-home")]
    [InlineData("configuration-cache")]
    [InlineData("operation-workspaces")]
    public async Task Partially_created_empty_storage_is_safe_to_resume(string store)
    {
        using var storage = new Initializer(); Directory.CreateDirectory(Path.Combine(storage.Data, store));
        var result = await storage.RunAsync(); Assert.True(result.Exit == 0, result.Error);
        Assert.Equal(Stores.Length, Directory.EnumerateDirectories(storage.Data).Count());
    }

    [LinuxInitializationTheory]
    [InlineData("unrelated.txt", false)]
    [InlineData(".hidden", false)]
    [InlineData("..hidden", false)]
    [InlineData("unrelated-empty-directory", true)]
    [InlineData(".hidden-directory", true)]
    public async Task Unknown_data_is_refused_before_ownership_or_permissions_are_changed(string name, bool directory)
    {
        using var storage = new Initializer(); var path = Path.Combine(storage.Data, name);
        if (directory) Directory.CreateDirectory(path); else await File.WriteAllTextAsync(path, "untouched user data");
        var result = await storage.RunAsync();
        Assert.Equal(1, result.Exit); Assert.Contains("unknown or nonempty", result.Error);
        Assert.False(File.Exists(storage.ChownLog));
        if (!directory) Assert.Equal("untouched user data", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(Directory.EnumerateDirectories(storage.Data), x => Stores.Contains(Path.GetFileName(x)));
    }

    [LinuxInitializationTheory]
    [InlineData("request-ledger")]
    [InlineData("credentials")]
    [InlineData("runtime-home")]
    [InlineData("configuration-cache")]
    [InlineData("operation-workspaces")]
    public async Task Nonempty_known_stores_are_not_claimed_without_an_identity(string store)
    {
        using var storage = new Initializer(); var path = Path.Combine(storage.Data, store, ".existing");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "keep");
        var result = await storage.RunAsync();
        Assert.Equal(1, result.Exit); Assert.False(File.Exists(storage.ChownLog));
        Assert.Equal("keep", await File.ReadAllTextAsync(path));
    }

    [LinuxInitializationTheory]
    [InlineData("identity.pk8", false)]
    [InlineData("identity.pk8", true)]
    [InlineData("request-ledger", false)]
    [InlineData("credentials", false)]
    [InlineData("runtime-home", false)]
    [InlineData("configuration-cache", false)]
    [InlineData("operation-workspaces", false)]
    [InlineData("operation-workspaces", true)]
    public async Task Symlinks_including_dangling_links_are_rejected_before_any_chown(string name, bool dangling)
    {
        using var storage = new Initializer(); var outside = Path.Combine(storage.Root, "outside");
        Directory.CreateDirectory(outside); await File.WriteAllTextAsync(Path.Combine(outside, "keep"), "untouched");
        var link = Path.Combine(storage.Data, name);
        Directory.CreateSymbolicLink(link, dangling ? Path.Combine(storage.Root, "missing") : outside);
        var result = await storage.RunAsync();
        Assert.Equal(1, result.Exit); Assert.False(File.Exists(storage.ChownLog));
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(outside, "keep")));
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    [LinuxInitializationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_identity_and_workload_data_are_preserved_across_reinitialization(bool wrongOwner)
    {
        using var storage = new Initializer();
        using var identity = AgentIdentity.LoadOrCreate(storage.Data);
        var before = await File.ReadAllBytesAsync(Path.Combine(storage.Data, "identity.pk8"));
        var workload = Path.Combine(storage.Data, "operation-workspaces", "running", "app.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(workload)!); await File.WriteAllTextAsync(workload, "running workload");
        var result = await storage.RunAsync(wrongOwner ? "1000" : "1654");
        Assert.Equal(wrongOwner ? 1 : 0, result.Exit);
        if (wrongOwner) Assert.False(File.Exists(storage.ChownLog));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(storage.Data, "identity.pk8")));
        Assert.Equal("running workload", await File.ReadAllTextAsync(workload));
    }

    [LinuxInitializationTheory]
    [InlineData("identity.pk8")]
    [InlineData("credentials")]
    [InlineData("operation-workspaces")]
    public async Task Invalid_storage_types_are_rejected_without_modification(string name)
    {
        using var storage = new Initializer(); var path = Path.Combine(storage.Data, name);
        if (name == "identity.pk8") Directory.CreateDirectory(path); else await File.WriteAllTextAsync(path, "not a directory");
        var result = await storage.RunAsync(); Assert.Equal(1, result.Exit);
        Assert.False(File.Exists(storage.ChownLog));
    }

    // Run the actual checked-in inline initializer without Docker or root. Only chown/stat are
    // substituted so tests cannot change real ownership and can cover both ownership decisions.
    private sealed class Initializer : IDisposable
    {
        public string Root { get; } = Path.Combine("/tmp/opencode", "stanza-initializer-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "data");
        public string ChownLog => Path.Combine(Root, "chown.log");
        public Initializer()
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            Directory.CreateDirectory(Data); var bin = Directory.CreateDirectory(Path.Combine(Root, "bin")).FullName;
            File.WriteAllText(Path.Combine(bin, "chown"), "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$TEST_CHOWN_LOG\"\n");
            File.WriteAllText(Path.Combine(bin, "stat"), "#!/bin/sh\n[ \"$1\" = '-c' ] && [ \"$2\" = '%u' ] || exit 2\nprintf '%s\\n' \"$TEST_AGENT_OWNER\"\n");
            foreach (var executable in Directory.GetFiles(bin)) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public async Task<(int Exit, string Error)> RunAsync(string owner = "1654")
        {
            var manifest = File.ReadAllText(Path.Combine(ArchitectureTests.RepositoryRoot(), "compose.agent.yaml"));
            var block = Regex.Match(manifest, @"(?m)^      - \|\r?\n(?<script>(?:^        .*\r?\n)+)");
            Assert.True(block.Success, "The inline initializer must be exercised rather than a copied implementation.");
            var script = string.Join('\n', block.Groups["script"].Value.Split('\n').Where(line => line.Length > 0).Select(line => line[8..].TrimEnd('\r')))
                .Replace("$$", "$", StringComparison.Ordinal).Replace("/agent-data", Data, StringComparison.Ordinal);
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-ec"); start.ArgumentList.Add(script);
            start.Environment["PATH"] = Path.Combine(Root, "bin") + ":/usr/bin:/bin";
            start.Environment["TEST_CHOWN_LOG"] = ChownLog; start.Environment["TEST_AGENT_OWNER"] = owner;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            await stdout; return (process.ExitCode, await stderr);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
