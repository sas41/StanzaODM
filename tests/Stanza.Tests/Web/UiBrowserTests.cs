using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Core.Security;
using Stanza.Tests.Automation;

namespace Stanza.Tests.Web;

public sealed class UiBrowserTests
{
    [BrowserFact]
    public async Task ApplicationUsesDesktopWidthAndConsistentControlsWithoutOverflowAndRemainsUsableOnMobile()
    {
        await using var original = new StanzaFactory();
        await using var configured = AutomationHttpTests.Configure(original);
        await using var factory = configured.WithWebHostBuilder(builder => builder.UseUrls("http://127.0.0.1:0").ConfigureServices(services =>
        { services.RemoveAll<IAgentCommandDispatcher>(); services.AddSingleton<IAgentCommandDispatcher, UiDispatcher>(); }));
        factory.UseKestrel(0); factory.StartServer();
        var address = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Guid deployment, agent, action, qa;
        var operation = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider; var accounts = services.GetRequiredService<AccountService>();
            var owner = (await accounts.BootstrapAsync("owner", "a-secure-password-123")).Id;
            qa = (await accounts.CreateAsync(owner, "qa", "a-secure-password-123", AccountRole.User)).Id;
            await services.GetRequiredService<AccessService>().CreateGroupAsync(owner, "Quality assurance");
            agent = Guid.NewGuid(); var db = services.GetRequiredService<StanzaDbContext>();
            db.Add(new AgentEntity { Id = agent, Identity = "ui-test-agent", Hostname = "local-agent-1", Status = "approved",
                ProtectedEnvironment = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Stanza.AgentEnvironment.v1", agent.ToString("N")).Protect("ENVIRONMENT=testing\n") });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var drafts = services.GetRequiredService<DeploymentDraftService>();
            deployment = (await drafts.CreateAsync(owner, "Dev 1", agent, "services:\n  app:\n    image: stanza-dev1:local\n    build:\n      context: ./sources/app\n", "ASPNETCORE_ENVIRONMENT=Development\n")).Id;
            var staging = (await drafts.CreateAsync(owner, "Staging", agent, "services:\n  app:\n    image: ghcr.io/example/app:v1.0.0\n", "ENVIRONMENT=staging\n")).Id;
            db.AddRange(new DeploymentRuntimeEntity { DeploymentId = deployment, AgentId = agent, Revision = 1, ObservedState = "running", DesiredState = "running" },
                new DeploymentRuntimeEntity { DeploymentId = staging, AgentId = agent, Revision = 1, ObservedState = "running", DesiredState = "running" });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var sources = services.GetRequiredService<SourceService>();
            await sources.SaveGitAsync(owner, deployment, null, 0, new("https://github.com/example/application.git", "main", "sources/app", null));
            await sources.SaveCredentialAsync(owner, null, 0, new("Example private access", "git", "https://github.com/example/private.git", "example-user", "example-test-token"));
            var automation = services.GetRequiredService<AutomationService>();
            action = (await automation.SaveActionAsync(owner, null, 0, "Dev deployment", true, "queue", new([new("deploy", deployment)],
                [new("branch", Default: "main", Input: "branch", Label: "Git branch", Source: "sources/app")], deployment, "Deploy Latest"))).Id;
            var reset = await automation.SaveActionAsync(owner, null, 0, "Reset DB", true, "queue", new([new("container-exec", deployment,
                Execution: new("/usr/bin/reset-db", ["--testing"], Service: "app"))], DeploymentId: deployment, Confirmation: "Reset the testing database?"));
            await automation.SaveActionAsync(owner, null, 0, "Restart", true, "queue", new([new("restart", deployment)], DeploymentId: deployment));
            await automation.SaveActionAsync(owner, null, 0, "Change Version", true, "queue", new([new("deploy", staging)],
                [new("version", Default: "v1.0.0", Input: "version", Repository: "ghcr.io/example/app", Services: ["app"])], staging));
            await automation.SaveActionScheduleAsync(owner, reset.Id, Guid.Empty, 0, "Nightly database reset", "0 3 * * *", "UTC", false, "skip", "skip");
            db.Add(new OperationEntity { Id = operation, ActorId = owner, DeploymentId = deployment, AgentId = agent, Kind = "deploy", State = "Failed", Code = "runtime_failed" });
            await db.SaveChangesAsync();
            await services.GetRequiredService<OperationOutputStore>().AppendAsync(operation, new(1, "stderr", "manifest unavailable: deployment failure details\n"));
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await using var browser = await UiBrowser.StartAsync(address, timeout.Token);
        await browser.GoAsync("/account/login");
        await browser.EvaluateAsync("document.querySelector('#Username').value='owner'; document.querySelector('#Password').value='a-secure-password-123'; document.querySelector('form').requestSubmit(); true");
        await browser.WaitAsync("location.pathname==='/' && document.readyState==='complete' && !!document.querySelector('[data-action-overview]')");
        var routes = new[] { "/", "/agents", "/deployments", "/deployments/create", $"/deployments/{deployment}", $"/deployments/{deployment}/edit", $"/deployments/{deployment}/file",
            "/sources", $"/sources/{deployment}", $"/sources/{deployment}/edit", "/credentials", "/credentials/create", "/automation", "/automation/actions/edit",
            $"/automation/actions/edit?id={action}", $"/automation/actions/{action}/access", "/automation/schedules/edit", "/automation/webhooks/edit", "/automation/images/edit", "/automation/limits",
            "/operations", $"/operations/{operation}", "/settings", $"/agents/{agent}/policy", $"/agents/{agent}/environment", "/users", "/users/create", $"/users/{qa}/access", "/groups", "/audit", "/portability", "/recovery", "/account/password" };
        foreach (var size in new[] { (1920, 1080), (1366, 768), (390, 844) })
        {
            await browser.ResizeAsync(size.Item1, size.Item2);
            foreach (var route in routes)
            {
                await browser.GoAsync(route);
                if (route == $"/operations/{operation}") await browser.WaitAsync("document.querySelector('[data-operation-output]')?.textContent.includes('deployment failure details')");
                if (route == $"/sources/{deployment}") await browser.WaitAsync("!!document.querySelector('[data-branch-choice]')");
                var metrics = await browser.EvaluateAsync("JSON.stringify({width:innerWidth,overflow:document.documentElement.scrollWidth-innerWidth,padding:parseFloat(getComputedStyle(document.querySelector('.content')).paddingLeft),maxWidth:getComputedStyle(document.querySelector('.content')).maxWidth,active:document.querySelectorAll('.sidebar nav [aria-current=page]').length,unstyledButtons:Array.from(document.querySelectorAll('.content button')).filter(b=>parseFloat(getComputedStyle(b).borderRadius)<4).length,largeChecks:Array.from(document.querySelectorAll('.content input[type=checkbox]')).filter(c=>c.getBoundingClientRect().width>20).length})");
                using var data = JsonDocument.Parse(metrics.GetString()!); var m = data.RootElement;
                Assert.True(m.GetProperty("overflow").GetDouble() <= 1, $"Horizontal page overflow at {size} on {route}: {metrics.GetString()}");
                Assert.Equal("none", m.GetProperty("maxWidth").GetString()); Assert.True(m.GetProperty("padding").GetDouble() <= 28);
                Assert.Equal(1, m.GetProperty("active").GetInt32()); Assert.Equal(0, m.GetProperty("unstyledButtons").GetInt32()); Assert.Equal(0, m.GetProperty("largeChecks").GetInt32());
                if (route == "/deployments/create")
                {
                    var parallel = await browser.EvaluateAsync("(()=>{const c=document.querySelector('.editor-columns');return getComputedStyle(c).gridTemplateColumns.split(' ').length;})()");
                    Assert.Equal(size.Item1 > 760 ? 2 : 1, parallel.GetInt32());
                }
                if (route == "/policy") Assert.True((await browser.EvaluateAsync("document.querySelectorAll('.form-grid > .field').length>6")).GetBoolean());
                if (route == "/operations") Assert.True((await browser.EvaluateAsync("!!document.querySelector('.table-scroll[tabindex=\"0\"] table')")).GetBoolean());
                if (route is "/" or "/credentials/create" or "/deployments/create" || route == $"/automation/actions/edit?id={action}")
                    await browser.ScreenshotAsync(size.Item1 + "-" + (route == "/" ? "overview" : route.StartsWith("/automation", StringComparison.Ordinal) ? "action-editor" : route.Trim('/').Replace('/', '-')));
            }
            await browser.GoAsync("/");
            await browser.EvaluateAsync("Array.from(document.querySelectorAll('button[hx-get]')).find(b=>b.textContent==='Deploy Latest').click(); true");
            await browser.WaitAsync("document.querySelector('#action-dialog').open && document.querySelector('[data-choice-select]')?.value==='main'");
            var dialogFits = await browser.EvaluateAsync("(()=>{const r=document.querySelector('#action-dialog').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight;})()");
            Assert.True(dialogFits.GetBoolean());
            await browser.ScreenshotAsync(size.Item1 + "-branch-modal");
            await browser.EvaluateAsync("document.querySelector('[data-close-action]').click(); true");
            if (size.Item1 < 760)
            {
                await browser.EvaluateAsync("document.querySelector('.nav-toggle').click(); true");
                await browser.WaitAsync("document.querySelector('.nav-toggle').getAttribute('aria-expanded')==='true' && document.querySelector('#primary-navigation').getBoundingClientRect().height>0");
                await browser.EvaluateAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true})); true");
                Assert.True((await browser.EvaluateAsync("document.activeElement===document.querySelector('.nav-toggle') && !document.querySelector('.sidebar').hasAttribute('data-open')")).GetBoolean());
            }
        }
    }

    private sealed class UiDispatcher : IAgentCommandDispatcher
    {
        public bool IsConnected(Guid agentId) => true;
        public Task<AgentWorkResponse> DispatchAsync(Guid agentId, AgentWorkRequest request, CancellationToken token = default) =>
            Task.FromResult(new AgentWorkResponse(request.Id, true, "ok", JsonSerializer.Serialize(new[] { "main", "feature/qa", "develop" })));
    }

    private sealed class UiBrowser(Process process, string profile, ClientWebSocket socket, string address, CancellationToken ct, Task<string> errors, Task<string> output) : IAsyncDisposable
    {
        private int next;
        public static async Task<UiBrowser> StartAsync(string address, CancellationToken ct)
        {
            var profile = Path.Combine("/tmp/opencode", "stanza-ui-browser-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(profile);
            var start = new ProcessStartInfo(BrowserFactAttribute.Binary) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--disable-background-networking", "--disable-extensions", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--window-size=1920,1080", "--user-data-dir=" + profile, new Uri(new Uri(address), "/account/login").ToString() }) start.ArgumentList.Add(argument);
            var process = Process.Start(start)!; var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            var portFile = Path.Combine(profile, "DevToolsActivePort"); var port = 0;
            while (port == 0)
            {
                ct.ThrowIfCancellationRequested(); if (process.HasExited) throw new InvalidOperationException("Chromium did not start: " + await errors);
                if (File.Exists(portFile)) { var lines = await File.ReadAllLinesAsync(portFile, ct); if (lines.Length > 0) int.TryParse(lines[0], out port); }
                if (port == 0) await Task.Delay(50, ct);
            }
            using var http = new HttpClient(); using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct));
            var page = Assert.Single(targets.RootElement.EnumerateArray(), x => x.GetProperty("type").GetString() == "page");
            var socket = new ClientWebSocket(); await socket.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), ct);
            return new(process, profile, socket, address, ct, errors, output);
        }
        private async Task<JsonElement> CommandAsync(string method, object parameters)
        {
            var id = ++next; await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters }).AsMemory(), WebSocketMessageType.Text, true, ct);
            var buffer = new byte[8192];
            while (true)
            {
                using var message = new MemoryStream(); ValueWebSocketReceiveResult frame;
                do { frame = await socket.ReceiveAsync(buffer.AsMemory(), ct); if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Browser transport closed."); if (message.Length + frame.Count > 8 * 1024 * 1024) throw new IOException("Browser response too large."); message.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
                using var response = JsonDocument.Parse(message.ToArray());
                if (!response.RootElement.TryGetProperty("id", out var result) || result.GetInt32() != id) continue;
                if (response.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
                return response.RootElement.GetProperty("result").Clone();
            }
        }
        public async Task<JsonElement> EvaluateAsync(string expression)
        {
            var result = await CommandAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true });
            if (result.TryGetProperty("exceptionDetails", out var error)) throw new InvalidOperationException(error.ToString());
            return result.GetProperty("result").GetProperty("value").Clone();
        }
        public async Task WaitAsync(string expression)
        {
            var started = Stopwatch.GetTimestamp();
            while (!(await EvaluateAsync(expression)).GetBoolean())
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(12)) throw new InvalidOperationException("UI did not reach " + expression + ": " + (await EvaluateAsync("location.href+' '+document.body.textContent.slice(-3000)")).GetString());
                await Task.Delay(50, ct);
            }
        }
        public async Task GoAsync(string path)
        {
            await CommandAsync("Page.navigate", new { url = new Uri(new Uri(address), path).ToString() });
            await WaitAsync("location.pathname+location.search===" + JsonSerializer.Serialize(path) + " && document.readyState==='complete' && !!document.querySelector('.content')");
        }
        public Task<JsonElement> ResizeAsync(int width, int height) => CommandAsync("Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile = width < 760 });
        public async Task ScreenshotAsync(string name)
        {
            var result = await CommandAsync("Page.captureScreenshot", new { format = "png" }); var bytes = Convert.FromBase64String(result.GetProperty("data").GetString()!);
            Assert.True(bytes.Length > 10000);
            if (Environment.GetEnvironmentVariable("STANZA_UI_SCREENSHOTS") is { Length: > 0 } directory)
            { Directory.CreateDirectory(directory); await File.WriteAllBytesAsync(Path.Combine(directory, name + ".png"), bytes, ct); }
        }
        public async ValueTask DisposeAsync()
        {
            socket.Dispose(); if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); await errors; await output; process.Dispose(); Directory.Delete(profile, true);
        }
    }
}
