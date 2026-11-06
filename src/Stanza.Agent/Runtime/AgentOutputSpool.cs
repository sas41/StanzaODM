using System.Buffers.Binary;
using System.Text.Json;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Runtime;

/// <summary>One append-only temporary file. The sparse index is memory-only and can be rebuilt.</summary>
public class AgentOutputSpool
{
    private const int IndexInterval = 64;
    private const int MaximumRecordBytes = 256 * 1024;
    private readonly string path;
    private readonly List<long> offsets = [];
    private long length;
    public long LastSequence { get; private set; }
    public bool Incomplete { get; private set; }

    public AgentOutputSpool(string path)
    {
        this.path = path;
        PrivateStorage.RejectLinks(path);
        if (!File.Exists(path)) return;
        using var stream = Open(FileAccess.Read);
        while (stream.Position < stream.Length)
        {
            var start = stream.Position;
            try
            {
                var chunk = ReadRecord(stream);
                if (chunk.Sequence != LastSequence + 1) throw new IOException("Noncontiguous output spool.");
                if (LastSequence % IndexInterval == 0) offsets.Add(start);
                LastSequence = chunk.Sequence;
                length = stream.Position;
            }
            catch (Exception error) when (error is EndOfStreamException or JsonException)
            { Incomplete = true; break; }
        }
    }

    private FileStream Open(FileAccess access)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var options = new FileStreamOptions { Mode = access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate,
            Access = access, Share = FileShare.Read };
        if (access != FileAccess.Read) options.UnixCreateMode = PrivateStorage.FileMode;
        return new(path, options);
    }

    public void Append(AgentOutputChunk chunk)
    {
        if (chunk.Sequence != LastSequence + 1) throw new IOException("Noncontiguous output spool.");
        PrivateStorage.RejectLinks(path);
        using var stream = Open(FileAccess.Write);
        stream.SetLength(length);
        stream.Position = length;
        try
        {
            WriteRecord(stream, chunk);
            stream.Flush();
            if (LastSequence % IndexInterval == 0) offsets.Add(length);
            length = stream.Position;
            LastSequence = chunk.Sequence;
        }
        catch
        {
            Incomplete = true;
            try { stream.SetLength(length); stream.Flush(); } catch (IOException) { }
            throw;
        }
    }

    protected virtual void WriteRecord(Stream stream, AgentOutputChunk chunk)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(chunk);
        if (content.Length > MaximumRecordBytes) throw new IOException("Output record exceeds limit.");
        Span<byte> header = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, content.Length);
        stream.Write(header); stream.Write(content);
    }

    private static AgentOutputChunk ReadRecord(Stream stream)
    {
        Span<byte> header = stackalloc byte[4]; stream.ReadExactly(header);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > MaximumRecordBytes) throw new IOException("Invalid output spool record.");
        var content = new byte[size]; stream.ReadExactly(content);
        return JsonSerializer.Deserialize<AgentOutputChunk>(content) ?? throw new IOException("Invalid output spool record.");
    }

    public IReadOnlyList<AgentOutputChunk> Read(long after, int count = 64)
    {
        if (after < 0 || count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(after));
        if (after >= LastSequence) return [];
        PrivateStorage.RejectLinks(path);
        using var stream = Open(FileAccess.Read);
        stream.Position = offsets[checked((int)(after / IndexInterval))];
        var result = new List<AgentOutputChunk>();
        while (stream.Position < length && result.Count < count)
        {
            var chunk = ReadRecord(stream);
            if (chunk.Sequence > after) result.Add(chunk);
        }
        return result;
    }

    public void FlushDurable()
    {
        PrivateStorage.RejectLinks(path);
        if (!File.Exists(path) && length == 0) return;
        using var stream = Open(FileAccess.Write);
        stream.SetLength(length);
        stream.Flush(true);
        AgentWorkExecutor.FlushDirectory(Path.GetDirectoryName(path)!);
    }
}
