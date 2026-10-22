namespace Stanza.Tests.Agent;

internal sealed class AgentTestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine("/tmp/opencode", "stanza-agent-test-" + Guid.NewGuid().ToString("N"));

    public AgentTestDirectory() => Directory.CreateDirectory(Path);

    public void Dispose() => Directory.Delete(Path, true);
}

internal static class AgentTestWait
{
    public static async Task UntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (!condition()) { await timer.WaitForNextTickAsync(cancellation.Token); }
    }
}
