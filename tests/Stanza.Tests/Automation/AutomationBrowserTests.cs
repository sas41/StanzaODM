using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Automation;

public sealed class AutomationBrowserTests
{
    [BrowserFact]
    public async Task BrowserCanCreateActionPreviewAndSaveScheduleAndConfigureWebhookAndImages()
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        factory.UseKestrel(0); factory.StartServer();
        var address = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Guid owner;
        using (var scope = factory.Services.CreateScope()) owner = (await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
        var directory = Path.Combine(Directory.Exists("/tmp/opencode") ? "/tmp/opencode" : Path.GetTempPath(), "stanza-automation-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? browser = null; Task<string>? errors = null, output = null;
        try
        {
            var start = new ProcessStartInfo(BrowserFactAttribute.Binary) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--disable-background-networking",
                "--disable-extensions", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--window-size=1280,900",
                "--user-data-dir=" + directory, new Uri(new Uri(address), "/account/login").ToString() }) start.ArgumentList.Add(argument);
            browser = Process.Start(start) ?? throw new InvalidOperationException("Could not start Chromium.");
            errors = browser.StandardError.ReadToEndAsync(); output = browser.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = timeout.Token;
            var portFile = Path.Combine(directory, "DevToolsActivePort"); var port = 0;
            while (port == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (browser.HasExited) throw new InvalidOperationException("Chromium exited before its test transport was ready.");
                if (File.Exists(portFile)) { var lines = await File.ReadAllLinesAsync(portFile, ct); if (lines.Length >= 2) int.TryParse(lines[0], out port); }
                if (port == 0) await Task.Delay(100, ct);
            }
            using var http = new HttpClient();
            using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct));
            var page = Assert.Single(targets.RootElement.EnumerateArray(), x => x.GetProperty("type").GetString() == "page");
            using var socket = new ClientWebSocket(); await socket.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), ct);
            var chrome = new Chrome(socket, ct);
            await chrome.Wait("!!document.querySelector('#Password')");
            await chrome.Eval("document.querySelector('#Username').value='owner'; document.querySelector('#Password').value='a-secure-password-123'; setTimeout(()=>document.querySelector('form').requestSubmit(),0); true");
            await chrome.Wait("location.pathname==='/'");
            var deployment = Guid.NewGuid();
            await chrome.Go(address, "/automation/actions/edit", "!!document.querySelector('#PlanJson')");
            var plan = JsonSerializer.Serialize(new ActionPlan([new("validate", deployment)]), AutomationRules.Json);
            await chrome.Eval("document.querySelector('#Name').value='Browser ordered action'; document.querySelector('#PlanJson').value=" + JsonSerializer.Serialize(plan) + "; document.querySelector('#Enabled').checked=true; setTimeout(()=>document.querySelector('form[action=\"/automation/actions/edit\"]').requestSubmit(),0); true");
            await chrome.Wait("location.pathname==='/automation' && document.body.textContent.includes('Browser ordered action')");
            await chrome.Go(address, "/automation/schedules/edit", "!!document.querySelector('#Expression')");
            await chrome.Eval($"document.querySelector('#Name').value='Browser durable schedule'; document.querySelector('#DeploymentId').value='{deployment}'; document.querySelector('#Expression').value='*/5 * * * *'; document.querySelector('#TimeZone').value='UTC'; document.querySelector('button[hx-post]').click(); true");
            await chrome.Wait("document.querySelector('#occurrences').textContent.includes('Upcoming occurrences')");
            await chrome.Eval("setTimeout(()=>document.querySelector('form[action=\"/automation/schedules/edit\"]').requestSubmit(),0); true");
            await chrome.Wait("location.pathname==='/automation' && document.body.textContent.includes('Browser durable schedule')");
            await chrome.Go(address, "/automation/webhooks/edit", "!!document.querySelector('#ParameterMapping')");
            await chrome.Eval($"document.querySelector('#Name').value='Browser signed webhook'; document.querySelector('#DeploymentId').value='{deployment}'; document.querySelector('#Provider').value='generic'; setTimeout(()=>document.querySelector('form[action=\"/automation/webhooks/edit\"]').requestSubmit(),0); true");
            await chrome.Wait("document.body.textContent.includes('Copy your webhook signing secret now') && document.querySelector('pre')?.textContent.length===64");
            await chrome.Go(address, "/automation/images/edit", "!!document.querySelector('#Services')");
            await chrome.Eval($"document.querySelector('#Name').value='Browser registry images'; document.querySelector('#DeploymentId').value='{deployment}'; document.querySelector('#Services').value='app,worker'; setTimeout(()=>document.querySelector('form[action=\"/automation/images/edit\"]').requestSubmit(),0); true");
            await chrome.Wait("location.pathname==='/automation' && document.body.textContent.includes('Browser registry images')");
            var screenshot = await chrome.Command("Page.captureScreenshot", new { format = "png" });
            Assert.True(Convert.FromBase64String(screenshot.GetProperty("data").GetString()!).Length > 10000);
            using var checkScope = factory.Services.CreateScope();
            var saved = await checkScope.ServiceProvider.GetRequiredService<AutomationService>().ListAsync(owner, ct);
            Assert.Single(saved.Actions); Assert.Single(saved.Schedules); Assert.Single(saved.Webhooks); Assert.Single(saved.ImagePolicies);
        }
        finally
        {
            if (browser is not null)
            {
                if (!browser.HasExited) browser.Kill(entireProcessTree: true);
                await browser.WaitForExitAsync(); if (errors is not null) await errors; if (output is not null) await output; browser.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Chrome(ClientWebSocket socket, CancellationToken ct)
    {
        private int next;
        public async Task<JsonElement> Command(string method, object parameters)
        {
            var id = ++next;
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters }).AsMemory(), WebSocketMessageType.Text, true, ct);
            var buffer = new byte[8192];
            while (true)
            {
                using var data = new MemoryStream(); ValueWebSocketReceiveResult frame;
                do { frame = await socket.ReceiveAsync(buffer.AsMemory(), ct); if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Browser transport closed."); if (data.Length + frame.Count > 4 * 1024 * 1024) throw new IOException("Browser response too large."); data.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
                using var response = JsonDocument.Parse(data.ToArray());
                if (!response.RootElement.TryGetProperty("id", out var resultId) || resultId.GetInt32() != id) continue;
                if (response.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("Browser command failed.");
                return response.RootElement.GetProperty("result").Clone();
            }
        }
        public async Task<JsonElement> Eval(string expression)
        {
            var response = await Command("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true });
            return response.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
        }
        public async Task Wait(string expression)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested(); if ((await Eval(expression)).ValueKind == JsonValueKind.True) return;
                var validation = await Eval("document.querySelector('.validation-summary-errors')?.textContent || ''");
                if (validation.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(validation.GetString()))
                    throw new InvalidOperationException("Browser form validation failed: " + validation.GetString());
                await Task.Delay(100, ct);
            }
        }
        public async Task Go(string address, string path, string ready)
        { await Command("Page.navigate", new { url = new Uri(new Uri(address), path).ToString() }); await Wait($"location.pathname==={JsonSerializer.Serialize(path)} && ({ready})"); }
    }
}
