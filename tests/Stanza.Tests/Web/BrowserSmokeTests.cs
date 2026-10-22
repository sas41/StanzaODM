using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Stanza.Tests.Web;

public sealed class BrowserSmokeTests
{
    [BrowserFact]
    public async Task FirstRunPageRendersInARealBrowser()
    {
        await using var factory = new StanzaFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        var address = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri(address) });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/setup")).StatusCode);
        var root = Directory.Exists("/tmp/opencode") ? "/tmp/opencode" : Path.GetTempPath();
        var directory = Path.Combine(root, $"stanza-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var screenshot = Path.Combine(directory, "setup.png");
        try
        {
            var info = new ProcessStartInfo(BrowserFactAttribute.Binary)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
            {
                "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--disable-background-networking",
                "--no-first-run", "--no-default-browser-check", "--window-size=1280,900", "--virtual-time-budget=1500",
                $"--user-data-dir={Path.Combine(directory, "profile")}", $"--screenshot={screenshot}", "--dump-dom",
                new Uri(client.BaseAddress!, "/setup").ToString()
            }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the browser.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var html = await output;
            var diagnostics = await errors;
            Assert.True(process.ExitCode == 0, diagnostics);
            Assert.Contains("Make it yours.", html);
            Assert.Contains("Create System Admin", html);
            Assert.Contains("name=\"__RequestVerificationToken\"", html);
            Assert.DoesNotContain("ERR_CONNECTION_REFUSED", html);
            Assert.True(File.Exists(screenshot), diagnostics);
            Assert.True(new FileInfo(screenshot).Length > 10_000, "The browser should render a nonempty setup screen.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}

public sealed class BrowserFactAttribute : FactAttribute
{
    public static string Binary => Environment.GetEnvironmentVariable("STANZA_BROWSER_BINARY") ?? "/usr/bin/chromium";
    public BrowserFactAttribute()
    {
        if (!File.Exists(Binary)) Skip = "Chromium is unavailable. Set STANZA_BROWSER_BINARY to run the real-browser smoke test.";
    }
}
