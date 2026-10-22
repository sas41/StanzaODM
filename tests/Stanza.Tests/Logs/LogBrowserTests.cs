using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Stanza.Tests.Web;

namespace Stanza.Tests.Logs;

public sealed class LogBrowserTests
{
    [BrowserFact]
    public async Task RealBrowserFollowsBoundedReadOnlyTextUnderProductionCspAndStopsOnRevocation()
    {
        await using var server = await LogTestServer.CreateAsync();
        var directory = Path.Combine("/tmp/opencode/stanza-logs-artifacts", "browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? browser = null;
        Task<string>? errors = null, output = null;
        try
        {
            var start = new ProcessStartInfo(BrowserFactAttribute.Binary) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--disable-background-networking", "--disable-extensions", "--no-first-run", "--remote-debugging-port=0", "--window-size=1280,900", $"--user-data-dir={directory}", server.Origin + "/test-login/" + server.Owner }) start.ArgumentList.Add(argument);
            browser = Process.Start(start)!;
            errors = browser.StandardError.ReadToEndAsync();
            output = browser.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var port = 0;
            while (port == 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (browser.HasExited) throw new InvalidOperationException("Chromium exited before the log test transport was ready.");
                var activePort = Path.Combine(directory, "DevToolsActivePort");
                if (File.Exists(activePort)) int.TryParse((await File.ReadAllLinesAsync(activePort, timeout.Token)).FirstOrDefault(), out port);
                if (port == 0) await Task.Delay(50, timeout.Token);
            }
            using var http = new HttpClient();
            using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", timeout.Token));
            var page = Assert.Single(targets.RootElement.EnumerateArray(), item => item.GetProperty("type").GetString() == "page");
            using var transport = new ClientWebSocket();
            await transport.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), timeout.Token);
            var chrome = new ChromeCommands(transport, timeout.Token);
            await chrome.WaitAsync("location.pathname.startsWith('/test-login/') && document.readyState === 'complete'");
            await chrome.CommandAsync("Page.navigate", new { url = server.Origin + "/logs/" + server.Deployment });
            await chrome.WaitAsync("!!document.querySelector('#logs-connect') && document.readyState === 'complete'");
            await chrome.EvaluateAsync("document.querySelector('[name=target]').value = 'web'; document.querySelector('#logs-connect').requestSubmit(); true");
            await chrome.WaitAsync("document.querySelector('#logs-status').textContent.startsWith('Following')");
            Assert.Equal("logs", Assert.Single(server.Transport.Opens).Execution.Mode);
            var bytes = Encoding.UTF8.GetBytes("😀漢 <img src=x onerror=alert(1)>\n");
            await server.Transport.Session.Output.Writer.WriteAsync(new(bytes.AsMemory(0, 2)), timeout.Token);
            await server.Transport.Session.Output.Writer.WriteAsync(new(bytes.AsMemory(2)), timeout.Token);
            await chrome.WaitAsync("document.querySelector('#container-logs').textContent.includes('😀漢 <img')");
            Assert.Equal(JsonValueKind.True, (await chrome.EvaluateAsync("document.querySelector('#container-logs img') === null && !document.querySelector('#container-logs').isContentEditable && !document.cookie.includes('Cookies')")).ValueKind);
            var chunk = Encoding.UTF8.GetBytes(new string('漢', 21000));
            for (var i = 0; i < 8; i++) await server.Transport.Session.Output.Writer.WriteAsync(new(chunk), timeout.Token);
            await server.Transport.Session.Output.Writer.WriteAsync(new(Encoding.UTF8.GetBytes("last-marker\n")), timeout.Token);
            await chrome.WaitAsync("document.querySelector('#container-logs').textContent.endsWith('last-marker\\n')");
            Assert.Equal(JsonValueKind.True, (await chrome.EvaluateAsync("new TextEncoder().encode(document.querySelector('#container-logs').textContent).length <= 262144 && !document.querySelector('#container-logs').textContent.includes('\\ufffd')")).ValueKind);
            await chrome.EvaluateAsync("document.querySelector('#logs-clear').click(); true");
            Assert.Equal("", (await chrome.EvaluateAsync("document.querySelector('#container-logs').textContent")).GetString());
            await server.MutateAsync(async db => (await db.Accounts.FindAsync(server.Owner))!.Enabled = false);
            server.Clock.Advance(TimeSpan.FromSeconds(15));
            await chrome.WaitAsync("document.querySelector('#logs-status').textContent.includes('revoked') && !document.querySelector('#logs-connect button[type=submit]').disabled");
            await server.Transport.Session.Disposed.Task.WaitAsync(timeout.Token);
            await Task.Delay(250, timeout.Token);
            Assert.Single(server.Transport.Opens);
            Assert.True(server.Transport.Session.Closed);
            Assert.Equal(0, server.Transport.Session.InputCalls);
            Assert.Equal(0, server.Transport.Session.ResizeCalls);
        }
        finally
        {
            if (browser is not null)
            {
                if (!browser.HasExited) browser.Kill(true);
                await browser.WaitForExitAsync();
                if (errors is not null) await errors;
                if (output is not null) await output;
                browser.Dispose();
            }
            Directory.Delete(directory, true);
        }
    }

    private sealed class ChromeCommands(ClientWebSocket socket, CancellationToken token)
    {
        private int id;
        public async Task<JsonElement> CommandAsync(string method, object parameters)
        {
            var requestId = ++id;
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id = requestId, method, @params = parameters }).AsMemory(), WebSocketMessageType.Text, true, token);
            var buffer = new byte[8192];
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(buffer.AsMemory(), token);
                    if (message.Length + frame.Count > 1024 * 1024) throw new InvalidOperationException("Browser test response too large.");
                    message.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                using var response = JsonDocument.Parse(message.ToArray());
                if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != requestId) continue;
                Assert.False(response.RootElement.TryGetProperty("error", out _));
                return response.RootElement.GetProperty("result").Clone();
            }
        }
        public async Task<JsonElement> EvaluateAsync(string expression)
        {
            var result = await CommandAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true });
            return result.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
        }
        public async Task WaitAsync(string expression)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((await EvaluateAsync(expression)).ValueKind != JsonValueKind.True)
            {
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Browser condition failed: " + expression);
                await Task.Delay(50, token);
            }
        }
    }
}
