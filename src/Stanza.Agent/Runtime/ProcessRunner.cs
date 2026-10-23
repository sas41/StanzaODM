using System.Diagnostics;
using System.Text;

namespace Stanza.Agent.Runtime;

public sealed record ProcessRequest(string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout, string? StandardInput = null,
    Func<string, string, Task>? OnOutput = null, bool CaptureOutput = true)
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
            request.Timeout != System.Threading.Timeout.InfiniteTimeSpan && (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromDays(7)) ||
            request.StandardInput?.Length > 1048576)
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
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var readerFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Kill()
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        async Task DrainAsync(StreamReader reader, StringBuilder target, string stream)
        {
            try
            {
                var buffer = new char[4096];
                var pending = "";
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), linked.Token);
                    if (count == 0)
                    {
                        if (pending.Length > 0 && request.OnOutput is not null) await request.OnOutput(stream, pending);
                        return;
                    }
                    if (request.CaptureOutput) target.Append(buffer, 0, count);
                    if (request.OnOutput is not null)
                    {
                        var text = pending + new string(buffer, 0, count);
                        pending = char.IsHighSurrogate(text[^1]) ? text[^1..] : "";
                        if (pending.Length > 0) text = text[..^1];
                        if (text.Length > 0) await request.OnOutput(stream, text);
                    }
                }
            }
            catch (Exception ex) { readerFailure.TrySetResult(ex); throw; }
        }

        var drains = Task.WhenAll(DrainAsync(process.StandardOutput, stdout, "stdout"), DrainAsync(process.StandardError, stderr, "stderr"));
        var timedOut = false;
        try
        {
            var exited = process.WaitForExitAsync(linked.Token);
            await Task.WhenAny(exited, readerFailure.Task);
            if (readerFailure.Task.IsCompleted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(await readerFailure.Task).Throw();
            await exited;
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
        finally
        {
            Kill();
            if (!drains.IsCompleted) await linked.CancelAsync();
            try { await drains; } catch (Exception) when (linked.IsCancellationRequested) { }
        }

        return new(process.ExitCode, stdout.ToString(), stderr.ToString(), false, timedOut);
    }
}
