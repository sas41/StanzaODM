using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Stanza.Core.Security;
using Stanza.ControlPlane.Security;

namespace Stanza.Tests.Web;

public sealed class BrowserAuthenticationTests
{
    [BrowserFact]
    public async Task ProductionFirstRunLoginAndLiveDashboardWorkOnLoopback()
    {
        await using var factory = new StanzaFactory(environment: "Production");
        factory.UseKestrel(0);
        factory.StartServer();
        var address = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var root = Directory.Exists("/tmp/opencode") ? "/tmp/opencode" : Path.GetTempPath();
        var directory = Path.Combine(root, $"stanza-browser-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Process? browser = null;
        Task<string>? errors = null;
        Task<string>? output = null;
        try
        {
            var start = new ProcessStartInfo(BrowserFactAttribute.Binary)
            {
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (var argument in new[]
            {
                "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--disable-background-networking",
                "--disable-extensions", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
                "--window-size=1280,900", $"--user-data-dir={directory}", new Uri(new Uri(address), "/setup").ToString()
            }) start.ArgumentList.Add(argument);
            browser = Process.Start(start) ?? throw new InvalidOperationException("Could not start Chromium.");
            errors = browser.StandardError.ReadToEndAsync();
            output = browser.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var activePort = Path.Combine(directory, "DevToolsActivePort");
            var port = 0;
            while (port == 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (browser.HasExited) throw new InvalidOperationException("Chromium exited before its local test transport was ready.");
                if (File.Exists(activePort))
                {
                    var lines = await File.ReadAllLinesAsync(activePort, timeout.Token);
                    if (lines.Length >= 2) int.TryParse(lines[0], out port);
                }
                if (port == 0) await Task.Delay(100, timeout.Token);
            }
            using var http = new HttpClient();
            using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", timeout.Token));
            var page = Assert.Single(targets.RootElement.EnumerateArray(), item => item.GetProperty("type").GetString() == "page");
            using var transport = new ClientWebSocket();
            await transport.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), timeout.Token);
            var chrome = new ChromeCommands(transport, timeout.Token);
            await chrome.WaitForAsync("location.pathname === '/setup' && !!document.querySelector('#ConfirmPassword')");
            await chrome.EvaluateAsync("document.querySelector('#Username').value='owner'; document.querySelector('#Password').value='a-secure-password-123'; document.querySelector('#ConfirmPassword').value='a-secure-password-123'; setTimeout(() => document.querySelector('form').requestSubmit(), 0); true");
            await chrome.WaitForAsync("location.pathname === '/account/login' && !!document.querySelector('#Password')");
            await chrome.EvaluateAsync("document.querySelector('#Username').value='owner'; document.querySelector('#Password').value='a-secure-password-123'; setTimeout(() => document.querySelector('form').requestSubmit(), 0); true");
            await chrome.WaitForAsync("location.pathname === '/' && document.querySelector('[data-database-status]')?.textContent === 'Connected' && document.querySelector('[data-agent-count]')?.textContent === '0'");
            var live = await chrome.EvaluateAsync("new Promise(resolve => { const socket = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/live/status'); const timer = setTimeout(() => { socket.close(); resolve(false); }, 5000); socket.onmessage = event => { clearTimeout(timer); const value = JSON.parse(event.data); socket.close(); resolve(value.databaseReady === true && value.agentCount === 0); }; socket.onerror = () => { clearTimeout(timer); socket.close(); resolve(false); }; })");
            Assert.Equal(JsonValueKind.True, live.ValueKind);
            var screenshot = await chrome.CommandAsync("Page.captureScreenshot", new { format = "png" });
            Assert.True(Convert.FromBase64String(screenshot.GetProperty("data").GetString()!).Length > 10_000);
            using var scope = factory.Services.CreateScope();
            var account = Assert.Single(await scope.ServiceProvider.GetRequiredService<AccountService>().ListAsync(timeout.Token));
            Assert.Equal(AccountRole.SystemAdmin, account.Role);
            Assert.Equal("owner", account.Username);
        }
        finally
        {
            if (browser is not null)
            {
                if (!browser.HasExited) browser.Kill(entireProcessTree: true);
                await browser.WaitForExitAsync();
                if (errors is not null) await errors;
                if (output is not null) await output;
                browser.Dispose();
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ChromeCommands(ClientWebSocket socket, CancellationToken token)
    {
        private int nextId;

        public async Task<JsonElement> CommandAsync(string method, object parameters)
        {
            var id = ++nextId;
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters }).AsMemory(), WebSocketMessageType.Text, true, token);
            var buffer = new byte[8192];
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(buffer.AsMemory(), token);
                    if (frame.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("The browser test transport closed.");
                    if (message.Length + frame.Count > 4 * 1024 * 1024) throw new InvalidOperationException("The browser test response exceeded its limit.");
                    message.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                using var response = JsonDocument.Parse(message.ToArray());
                if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                if (response.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("The browser could not execute a test command.");
                return response.RootElement.GetProperty("result").Clone();
            }
        }

        public async Task<JsonElement> EvaluateAsync(string expression)
        {
            var response = await CommandAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true });
            return response.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
        }

        public async Task WaitForAsync(string expression)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var result = await EvaluateAsync(expression);
                if (result.ValueKind == JsonValueKind.True) return;
                await Task.Delay(100, token);
            }
        }
    }
}
