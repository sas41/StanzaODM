using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Core.Security;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Automation;

public sealed class AutomationBrowserTests
{
    [BrowserFact]
    public async Task BrowserCanCreateActionPreviewAndSaveScheduleAndConfigureWebhookAndImages()
    {
        await using var original = new StanzaFactory();
        await using var configured = AutomationHttpTests.Configure(original);
        await using var factory = configured.WithWebHostBuilder(builder => builder.UseUrls("http://127.0.0.1:0").ConfigureServices(services =>
        { services.RemoveAll<IAgentCommandDispatcher>(); services.AddSingleton<IAgentCommandDispatcher, ChoiceDispatcher>(); }));
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
            Guid deployment;
            using (var seed = factory.Services.CreateScope())
                deployment = (await seed.ServiceProvider.GetRequiredService<DeploymentDraftService>().CreateAsync(owner, "Automation demo", null, "services: {}", "")).Id;
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

            // Exercise the real visual editor, HTMX modal, remote branch/tag selectors and QA grants.
            Guid dev, staging, versionAction, resetAction, pruneAction, qa;
            using (var seed = factory.Services.CreateScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<StanzaDbContext>();
                var agent = Guid.NewGuid(); db.Add(new AgentEntity { Id = agent, Identity = agent.ToString("N"), Hostname = "local-agent-1", Status = "approved" }); await db.SaveChangesAsync();
                var drafts = seed.ServiceProvider.GetRequiredService<DeploymentDraftService>();
                dev = (await drafts.CreateAsync(owner, "Dev 1", agent, "services: {app: {image: busybox:latest}}", "")).Id;
                staging = (await drafts.CreateAsync(owner, "Staging", agent, "services: {app: {image: busybox:latest}}", "")).Id;
                await seed.ServiceProvider.GetRequiredService<SourceService>().SaveGitAsync(owner, dev, null, 0, new("https://git.example.test/app.git", "main", "src", null));
                var automation = seed.ServiceProvider.GetRequiredService<AutomationService>();
                versionAction = (await automation.SaveActionAsync(owner, null, 0, "Change Version", true, "queue", new([new("deploy", staging)],
                    [new("version", Default: "v74", Input: "version", Repository: "ghcr.io/org/app", Services: ["app"])], staging))).Id;
                resetAction = (await automation.SaveActionAsync(owner, null, 0, "Reset DB", true, "queue", new([new("container-exec", dev,
                    Execution: new("/usr/bin/reset-db", ["--testing"], Service: "app"))], DeploymentId: dev, Confirmation: "Reset the testing database?"))).Id;
                pruneAction = (await automation.SaveActionAsync(owner, null, 0, "Docker cleanup", true, "queue",
                    new([new("host-exec", AgentId: agent, Execution: new("/usr/bin/docker", ["system", "prune", "--force"]))]))).Id;
                qa = (await seed.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(owner, "qa", "a-secure-password-123", AccountRole.User)).Id;
            }
            await chrome.Go(address, "/automation/actions/edit", "!!document.querySelector('[data-action-editor]:not([hidden])')");
            await chrome.Eval($"document.querySelector('#Name').value='Dev deploy'; document.querySelector('#Enabled').checked=true; const d=document.querySelector('[data-plan-deployment]'); d.value='{dev}'; d.dispatchEvent(new Event('change')); document.querySelector('[data-plan-label]').value='Deploy Latest'; document.querySelector('[data-add-input]').click(); const p=document.querySelector('[data-editor-inputs]'); p.querySelector('[data-field=name]').value='branch'; p.querySelector('[data-field=default]').value='main'; const input=p.querySelector('[data-field=input]'); input.value='branch'; input.dispatchEvent(new Event('change')); p.querySelector('[data-field=source]').value='src'; document.querySelector('form[action=\"/automation/actions/edit\"]').requestSubmit(); true");
            await chrome.Wait("location.pathname==='/automation' && document.body.textContent.includes('Dev deploy')");
            await chrome.Go(address, "/", "document.body.textContent.includes('local-agent-1 Deployments')");
            await chrome.Wait("!!window.htmx && document.readyState==='complete'");
            await chrome.Eval($"document.querySelector('button[hx-get=\"/automation/actions/{versionAction}/invoke\"]').click(); true");
            await chrome.Wait("document.querySelector('#action-dialog').open && document.querySelector('[data-choice-select]')?.value==='v74'");
            await chrome.Eval("const search=document.querySelector('[data-choice-search]'); search.value='v74'; search.dispatchEvent(new Event('input')); true");
            await chrome.Wait("document.querySelector('[data-choice-select]').options.length===2 && !document.querySelector('[data-choice-select]').disabled");
            await chrome.Eval("document.querySelector('[data-action-invocation]').requestSubmit(); true");
            await chrome.Wait("location.pathname.startsWith('/automation/runs/') && !!document.querySelector('[data-action-run]')");
            await chrome.Go(address, "/", "!!document.querySelector('[data-action-overview]')");
            await chrome.Wait("!!window.htmx && document.readyState==='complete'");
            await chrome.Eval("Array.from(document.querySelectorAll('button[hx-get]')).find(b=>b.textContent==='Deploy Latest').click(); true");
            await chrome.Wait("document.querySelector('#action-dialog').open && document.querySelector('[data-choice-select]')?.value==='main'");
            await chrome.Eval("document.querySelector('[data-choice-select]').value='feature/qa'; document.querySelector('[data-action-invocation]').requestSubmit(); true");
            await chrome.Wait("location.pathname.startsWith('/automation/runs/')");
            await chrome.Go(address, "/automation/actions/edit?id=" + pruneAction, "!!document.querySelector('[data-action-schedule]')");
            await chrome.Eval("const schedule=document.querySelector('[data-action-schedule]'); schedule.querySelector('[name=Name]').value='Nightly Docker cleanup'; schedule.querySelector('[name=Expression]').value='0 3 * * *'; schedule.querySelector('[name=TimeZone]').value='UTC'; schedule.querySelector('[name=Enabled]').checked=true; schedule.querySelector('button[hx-post]').click(); true");
            await chrome.Wait("document.querySelector('[id$=\"-preview\"]').textContent.includes('Upcoming occurrences')");
            await chrome.Eval("document.querySelector('[data-action-schedule]').requestSubmit(); true");
            await chrome.Wait("document.body.textContent.includes('Action schedule enabled') && document.querySelectorAll('[data-action-schedule]').length===2");
            Assert.True((await chrome.Eval("document.querySelector('[data-action-schedule] [name=Expression]').value==='0 3 * * *' && document.querySelector('[data-action-schedule] [name=Enabled]').checked")).GetBoolean());
            await chrome.Eval("const schedule=document.querySelector('[data-action-schedule]'); schedule.querySelector('[name=Enabled]').checked=false; schedule.requestSubmit(); true");
            await chrome.Wait("document.body.textContent.includes('Action schedule saved and paused') && !document.querySelector('[data-action-schedule] [name=Enabled]').checked");
            await chrome.Go(address, $"/automation/actions/{resetAction}/access", "!!document.querySelector('select[name=accountId]')");
            await chrome.Eval($"document.querySelector('select[name=accountId]').value='{qa}'; document.querySelector('select[name=accountId]').closest('form').requestSubmit(); true");
            await chrome.Wait("document.body.textContent.includes('qa · User')");
            await chrome.Eval("document.querySelector('.sidebar-footer form').requestSubmit(); true");
            await chrome.Wait("location.pathname==='/account/login'");
            await chrome.Eval("document.querySelector('#Username').value='qa'; document.querySelector('#Password').value='a-secure-password-123'; document.querySelector('form').requestSubmit(); true");
            await chrome.Wait("location.pathname==='/' && document.body.textContent.includes('Reset DB')");
            Assert.True((await chrome.Eval("!document.body.textContent.includes('Deploy Latest') && !document.body.textContent.includes('Change Version')")).GetBoolean());
            await chrome.Wait("!!window.htmx && document.readyState==='complete'");
            await chrome.Eval("document.querySelector('button[hx-get]').click(); true");
            await chrome.Wait("document.querySelector('#action-dialog').open && !!document.querySelector('[name=confirmed]')");
            await chrome.Eval("document.querySelector('[name=confirmed]').checked=true; document.querySelector('[data-action-invocation]').requestSubmit(); true");
            await chrome.Wait("location.pathname.startsWith('/automation/runs/') && !!document.querySelector('[data-action-run]')");
            using var verify = factory.Services.CreateScope();
            var invocation = await verify.ServiceProvider.GetRequiredService<AutomationService>().InvocationAsync(owner, versionAction, ct);
            Assert.Equal("v74", invocation.Values["version"]);
            var qaRun = await verify.ServiceProvider.GetRequiredService<AutomationService>().ReadRunAsync(qa, Guid.Parse((await chrome.Eval("document.querySelector('[data-action-run]').dataset.actionRun")).GetString()!), ct);
            Assert.Equal(qa, qaRun.Run.RequestedBy); Assert.Equal(owner, qaRun.Run.ActionOwnerId);
            var cleanupSchedule = Assert.Single(await verify.ServiceProvider.GetRequiredService<AutomationService>().ActionSchedulesAsync(owner, pruneAction, ct));
            Assert.Null(cleanupSchedule.DeploymentId); Assert.False(cleanupSchedule.Enabled); Assert.Equal("0 3 * * *", cleanupSchedule.Expression);
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

    private sealed class ChoiceDispatcher : IAgentCommandDispatcher
    {
        public bool IsConnected(Guid agentId) => true;
        public Task<AgentWorkResponse> DispatchAsync(Guid agentId, AgentWorkRequest request, CancellationToken token = default) => Task.FromResult(request.Operation switch
        {
            "git-action-branches" => new(request.Id, true, "ok", JsonSerializer.Serialize(new[] { "main", "feature/qa" })),
            "registry-tags" => new(request.Id, true, "ok", JsonSerializer.Serialize(Enumerable.Range(0, 75).Select(i => "v" + i).ToArray())),
            "resolve-action-inputs" => new AgentWorkResponse(request.Id, true, "ok", ResolvedSources: new Dictionary<string, string> { ["src"] = new('a', 40) },
                ResolvedImages: new Dictionary<string, string> { ["app"] = "sha256:" + new string('b', 64) }),
            _ => throw new InvalidOperationException("Unexpected work")
        });
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
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                ct.ThrowIfCancellationRequested(); if ((await Eval(expression)).ValueKind == JsonValueKind.True) return;
                var validation = await Eval("document.querySelector('.validation-summary-errors')?.textContent || ''");
                if (validation.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(validation.GetString()))
                    throw new InvalidOperationException("Browser form validation failed: " + validation.GetString());
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(12))
                    throw new InvalidOperationException("Browser did not reach " + expression + ": " + (await Eval("location.href+' '+document.body.textContent.slice(-6000)")).GetString());
                await Task.Delay(100, ct);
            }
        }
        public async Task Go(string address, string path, string ready)
        { await Command("Page.navigate", new { url = new Uri(new Uri(address), path).ToString() }); await Wait($"location.pathname+location.search==={JsonSerializer.Serialize(path)} && document.readyState==='complete' && ({ready})"); }
    }
}
