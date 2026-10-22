using System.Diagnostics;
using System.Text;

namespace Stanza.Agent.Runtime;

public sealed record ProcessRequest(string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout, int MaxOutputCharacters = 65536, string? StandardInput = null)
{
    public override string ToString() => "ProcessRequest { structured process invocation; arguments and environment redacted }";
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool OutputLimitExceeded, bool TimedOut)
{
    public override string ToString() => $"ProcessResult {{ ExitCode = {ExitCode}, OutputLimitExceeded = {OutputLimitExceeded}, TimedOut = {TimedOut} }}";
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner(TimeProvider timeProvider) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Path.IsPathFullyQualified(request.Executable) || !Path.IsPathFullyQualified(request.WorkingDirectory) ||
            request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(1) ||
            request.MaxOutputCharacters is < 1 or > 1048576 || request.StandardInput?.Length > 1048576)
        {
            throw new ArgumentException("Invalid process invocation limits or paths.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null
        };
        start.Environment.Clear();
        foreach (var pair in request.Environment) { start.Environment.Add(pair.Key, pair.Value); }
        foreach (var argument in request.Arguments) { start.ArgumentList.Add(argument); }

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Never expose OS error messages containing a command, path or credential.
            throw new IOException("The configured runtime executable could not be started.");
        }

        using var timeout = new CancellationTokenSource(request.Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        async Task WriteInputAsync()
        {
            if (request.StandardInput is null) return;
            try { await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), linked.Token); }
            finally { process.StandardInput.Close(); }
        }
        var input = WriteInputAsync();
        var gate = new object();
        var captured = 0;
        var limitExceeded = false;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        void Kill()
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        async Task DrainAsync(StreamReader reader, StringBuilder target)
        {
            var buffer = new char[4096];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), linked.Token);
                if (count == 0) { return; }
                lock (gate)
                {
                    var retained = Math.Min(count, request.MaxOutputCharacters - captured);
                    target.Append(buffer, 0, retained);
                    captured += retained;
                    if (count > retained) { limitExceeded = true; Kill(); }
                }
            }
        }

        var drains = Task.WhenAll(DrainAsync(process.StandardOutput, stdout), DrainAsync(process.StandardError, stderr));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
            await drains.WaitAsync(linked.Token);
            await input.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            timedOut = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            Kill();
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { throw new IOException("The runtime process could not be terminated within its cleanup limit."); }
            try { await drains; }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            try { await input; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { Kill(); }

        return new(process.ExitCode, stdout.ToString(), stderr.ToString(), limitExceeded, timedOut);
    }
}
