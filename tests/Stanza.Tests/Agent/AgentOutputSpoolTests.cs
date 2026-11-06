using System.Text.Json;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class AgentOutputSpoolTests
{
    [Fact]
    public void OneFileSupportsMidRunCatchupAndRebuildsItsIndexAfterRestart()
    {
        using var root = new AgentTestDirectory(); var path = Path.Combine(root.Path, "output");
        var spool = new AgentOutputSpool(path);
        for (var seq = 1; seq <= 150; seq++) spool.Append(new(seq, "stdout", $"line {seq}\n雪\0"));
        Assert.Single(Directory.GetFiles(root.Path));
        Assert.Equal(150, spool.LastSequence);
        Assert.Equal(Enumerable.Range(64, 64).Select(x => (long)x), spool.Read(63).Select(x => x.Sequence));
        Assert.Equal(Enumerable.Range(129, 22).Select(x => (long)x), spool.Read(128).Select(x => x.Sequence));
        spool.FlushDurable();
        var reopened = new AgentOutputSpool(path);
        Assert.Equal(150, reopened.LastSequence);
        Assert.Equal("line 150\n雪\0", Assert.Single(reopened.Read(149)).Text);
        Assert.Empty(reopened.Read(150));
    }

    [Fact]
    public void PartialAppendIsRolledBackWithoutLosingCommittedPrefix()
    {
        using var root = new AgentTestDirectory(); var path = Path.Combine(root.Path, "output");
        var spool = new FailingSpool(path);
        spool.Append(new(1, "stdout", "keep")); var length = new FileInfo(path).Length;
        spool.Fail = true;
        Assert.Throws<IOException>(() => spool.Append(new(2, "stderr", "partial")));
        Assert.Equal(length, new FileInfo(path).Length); Assert.Equal(1, spool.LastSequence); Assert.True(spool.Incomplete);
        Assert.Equal("keep", Assert.Single(new AgentOutputSpool(path).Read(0)).Text);
        spool.Fail = false; spool.Append(new(2, "stderr", "retry"));
        Assert.Equal("retry", Assert.Single(spool.Read(1)).Text);
    }

    [Fact]
    public void IncompleteTailDoesNotMasqueradeAsACompleteRecord()
    {
        using var root = new AgentTestDirectory(); var path = Path.Combine(root.Path, "output");
        var spool = new AgentOutputSpool(path); spool.Append(new(1, "stdout", "keep"));
        using (var append = new FileStream(path, FileMode.Append)) append.Write([50, 0, 0, 0, 123]);
        var reopened = new AgentOutputSpool(path);
        Assert.True(reopened.Incomplete); Assert.Equal(1, reopened.LastSequence);
        Assert.Single(reopened.Read(0));
    }

    [Fact]
    public void AtomicJournalWriteNeverPublishesAnEmptyOrPartialFinalFile()
    {
        using var root = new AgentTestDirectory(); var path = Path.Combine(root.Path, "job.json");
        var storage = new FailingJournal();
        Assert.Throws<IOException>(() => storage.Write(path, new { Code = "new" }));
        Assert.False(File.Exists(path)); Assert.Empty(Directory.GetFiles(root.Path));
        File.WriteAllText(path, "existing-evidence");
        Assert.Throws<IOException>(() => storage.Write(path, new { Code = "replace" }));
        Assert.Equal("existing-evidence", File.ReadAllText(path)); Assert.Single(Directory.GetFiles(root.Path));
    }

    private sealed class FailingSpool(string path) : AgentOutputSpool(path)
    {
        public bool Fail;
        protected override void WriteRecord(Stream stream, AgentOutputChunk chunk)
        {
            if (Fail) { stream.Write([1, 2, 3]); throw new IOException("simulated ENOSPC"); }
            base.WriteRecord(stream, chunk);
        }
    }
    private sealed class FailingJournal : AgentJournalStorage
    {
        protected override void Serialize<T>(Stream stream, T value)
        { stream.Write([123]); throw new IOException("simulated journal ENOSPC"); }
    }
}
