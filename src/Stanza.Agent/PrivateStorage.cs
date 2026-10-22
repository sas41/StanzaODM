namespace Stanza.Agent;

public static class PrivateStorage
{
    public const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Agent private storage currently requires Unix filesystem permissions.");
        }

        var fullPath = Path.GetFullPath(path);
        RejectLinks(fullPath);
        Directory.CreateDirectory(fullPath, DirectoryMode);
        File.SetUnixFileMode(fullPath, DirectoryMode);
        RejectLinks(fullPath);
        return fullPath;
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            // LinkTarget also detects dangling links, for which Exists returns false.
            if (new DirectoryInfo(current).LinkTarget is not null ||
                (File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Symbolic links are not permitted in Agent storage paths.");
            }
        }
    }
}
