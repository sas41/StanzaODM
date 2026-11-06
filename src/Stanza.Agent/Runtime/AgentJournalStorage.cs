using System.Text.Json;

namespace Stanza.Agent.Runtime;

public interface IAgentJournalStorage
{
    void Write<T>(string path, T value, bool replace = true);
}

public class AgentJournalStorage : IAgentJournalStorage
{
    public void Write<T>(string path, T value, bool replace = true)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        PrivateStorage.RejectLinks(path);
        var directory = Path.GetDirectoryName(path)!;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = PrivateStorage.FileMode
            }))
            {
                Serialize(stream, value);
                stream.Flush(true);
            }
            File.Move(temporary, path, replace);
            AgentWorkExecutor.FlushDirectory(directory);
        }
        finally
        {
            // Cleanup must not mask the original write/fsync exception.
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    protected virtual void Serialize<T>(Stream stream, T value) => JsonSerializer.Serialize(stream, value);
}
