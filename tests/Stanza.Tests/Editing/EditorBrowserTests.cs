using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using Stanza.Tests.Web;

namespace Stanza.Tests.Editing;

public sealed class EditorBrowserTests
{
    [BrowserFact]
    public async Task Highlighting_preserves_form_text_selection_and_blocks_html_injection_in_real_browser()
    {
        var root = new DirectoryInfo(SourceDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Stanza.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(Directory.Exists("/tmp/opencode") ? "/tmp/opencode" : Path.GetTempPath(), $"stanza-editor-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var script = new Uri(Path.Combine(root.FullName, "src/Stanza.ControlPlane/wwwroot/js/editor.js")).AbsoluteUri;
            const string original = "services:\n  app: {image: '<img src=x onerror=alert(1)>'}\n# preserved\n";
            var path = Path.Combine(directory, "editor.html");
            await File.WriteAllTextAsync(path, "<!doctype html><html><body><textarea name=ComposeText data-language=compose>" +
                WebUtility.HtmlEncode(original) + "</textarea><script src=\"" + script + "\"></script>" + """
                <script>
                document.addEventListener('DOMContentLoaded', () => {
                  const input = document.querySelector('textarea');
                  const original = input.value;
                  input.setSelectionRange(3, 8);
                  input.dispatchEvent(new Event('input'));
                  const mirror = document.querySelector('.syntax-preview');
                  const form = document.createElement('form');
                  document.body.appendChild(form); form.appendChild(input);
                  document.body.dataset.result = input.value === original && input.selectionStart === 3 && input.selectionEnd === 8 &&
                    mirror.textContent === original + '\n' && new FormData(form).get('ComposeText') === original &&
                    document.querySelectorAll('img').length === 0 && mirror.querySelectorAll('span').length > 0 ? 'passed' : 'failed';
                });
                </script></body></html>
                """);
            var info = new ProcessStartInfo(BrowserFactAttribute.Binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--disable-background-networking", "--no-first-run", "--allow-file-access-from-files", "--virtual-time-budget=1000", $"--user-data-dir={Path.Combine(directory, "profile")}", "--dump-dom", new Uri(path).AbsoluteUri })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Chromium.");
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw; }
            Assert.True(process.ExitCode == 0, await errors);
            Assert.Contains("data-result=\"passed\"", await output);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
}
