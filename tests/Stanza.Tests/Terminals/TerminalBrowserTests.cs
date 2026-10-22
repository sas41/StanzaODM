using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Stanza.Tests.Web;

namespace Stanza.Tests.Terminals;

public sealed class TerminalBrowserTests
{
    [BrowserFact]
    public async Task SelfHostedXtermEmulatesAnsiStreamsKeyboardAndFitsViewportUnderProductionCsp()
    {
        await using var server = await TerminalTestServer.CreateAsync();
        var directory = Path.Combine("/tmp/opencode/stanza-terminal-artifacts", "browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? browser = null;
        Task<string>? errors = null, output = null;
        try
        {
            var start = new ProcessStartInfo(BrowserFactAttribute.Binary) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--disable-background-networking", "--disable-extensions", "--no-first-run", "--remote-debugging-port=0", "--window-size=1280,900", $"--user-data-dir={directory}", server.Origin + "/login/" + server.Owner }) start.ArgumentList.Add(argument);
            browser = Process.Start(start)!;
            errors = browser.StandardError.ReadToEndAsync();
            output = browser.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var activePort = Path.Combine(directory, "DevToolsActivePort");
            var port = 0;
            while (port == 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (browser.HasExited) throw new InvalidOperationException("Chromium exited before the test transport was ready.");
                if (File.Exists(activePort)) int.TryParse((await File.ReadAllLinesAsync(activePort, timeout.Token)).FirstOrDefault(), out port);
                if (port == 0) await Task.Delay(50, timeout.Token);
            }
            using var http = new HttpClient();
            using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", timeout.Token));
            var page = Assert.Single(targets.RootElement.EnumerateArray(), item => item.GetProperty("type").GetString() == "page");
            using var transport = new ClientWebSocket();
            await transport.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), timeout.Token);
            var chrome = new ChromeCommands(transport, timeout.Token);
            await chrome.WaitAsync("location.pathname.startsWith('/login/') && document.readyState === 'complete'");
            await chrome.CommandAsync("Page.navigate", new { url = server.Origin + "/terminals/" + server.Deployment });
            await chrome.WaitAsync("!!document.querySelector('#terminal-connect') && typeof Terminal === 'function' && typeof FitAddon === 'object'");
            await chrome.EvaluateAsync("document.querySelector('[name=target]').value = 'web'; document.querySelector('#terminal-connect').requestSubmit(); true");
            await chrome.WaitAsync("document.querySelector('#terminal-status').textContent === 'Connected.' && !!document.querySelector('.xterm-helper-textarea')");
            var initialSize = await server.Transport.Session.Resizes.Reader.ReadAsync(timeout.Token);
            Assert.InRange(initialSize.Columns, 2, 500);
            Assert.InRange(initialSize.Rows, 1, 300);
            var text = "\u001b[2J\u001b[Hhello\u001b[31m red\u001b[0m <img src=x onerror=alert(1)>";
            await server.Transport.Session.Output.Writer.WriteAsync(new(Encoding.UTF8.GetBytes(text)), timeout.Token);
            await chrome.WaitAsync("document.querySelector('.xterm-rows').textContent.replaceAll('\\u00a0', ' ').includes('hello red <img')");
            Assert.Equal(JsonValueKind.True, (await chrome.EvaluateAsync("document.querySelector('#container-terminal img') === null && document.querySelector('.xterm-rows').textContent.indexOf('\\u001b') === -1 && document.cookie.indexOf('Cookies') === -1")).ValueKind);
            await chrome.EvaluateAsync("document.querySelector('.xterm-helper-textarea').focus(); true");
            await chrome.CommandAsync("Input.insertText", new { text = "typed" });
            Assert.Equal("typed", Encoding.UTF8.GetString(await server.Transport.Session.Input.Reader.ReadAsync(timeout.Token)));
            // Drain any initial observer notification before resizing the actual browser viewport.
            while (server.Transport.Session.Resizes.Reader.TryRead(out _)) { }
            await chrome.CommandAsync("Emulation.setDeviceMetricsOverride", new { width = 760, height = 500, deviceScaleFactor = 1, mobile = false });
            var resized = await server.Transport.Session.Resizes.Reader.ReadAsync(timeout.Token);
            Assert.True(resized.Columns != initialSize.Columns || resized.Rows != initialSize.Rows);
            await chrome.EvaluateAsync("document.querySelector('#terminal-disconnect').click(); true");
            await server.Transport.Session.Disposed.Task.WaitAsync(timeout.Token);
            Assert.True(server.Transport.Session.Closed);
            await chrome.WaitAsync("document.querySelector('#terminal-connect button[type=submit]').disabled === false");
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
                    if (message.Length + frame.Count > 1024 * 1024) throw new InvalidOperationException("Browser response too large.");
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
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Browser condition failed: " + expression + " Page: " + await EvaluateAsync("document.querySelector('.xterm-rows')?.outerHTML || document.documentElement.outerHTML.slice(0, 5000)"));
                await Task.Delay(50, token);
            }
        }
    }
}
