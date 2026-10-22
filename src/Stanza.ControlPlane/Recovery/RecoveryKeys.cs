using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Stanza.ControlPlane.Recovery;

internal static partial class RecoveryKeys
{
    public const int MaximumKeys = 1024, MaximumKeyBytes = 1024 * 1024;
    [GeneratedRegex(@"\Akey-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\.xml\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyName();

    public static List<RecoveryKey> Read(string directory)
    {
        EnsureDirectory(directory);
        var result = new List<RecoveryKey>();
        foreach (var file in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(file);
            if (!KeyName().IsMatch(name)) continue; // Only DP keys, never unrelated files or arbitrary managed paths.
            if (result.Count >= MaximumKeys) throw new RecoveryOperationException("The persisted key count exceeds the recovery limit.");
            ValidateFile(file);
            var content = ReadBounded(file);
            var key = new RecoveryKey { Name = name, Content = content };
            Validate(key);
            result.Add(key);
        }
        if (result.Count == 0) throw new RecoveryOperationException("No persisted encryption keys were found. A backup without its keys cannot recover secrets.");
        return result;
    }

    public static void Validate(RecoveryKey key)
    {
        if (key is null || key.Name is null || !KeyName().IsMatch(key.Name) || key.Content is null || key.Content.Length is < 1 or > MaximumKeyBytes)
            throw new RecoveryOperationException("The archive contains an invalid encryption-key entry.");
        try
        {
            using var stream = new MemoryStream(key.Content, false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumKeyBytes });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != "key" || !Guid.TryParse(document.Root.Attribute("id")?.Value, out var id)
                || !key.Name.Equals($"key-{id:D}.xml", StringComparison.OrdinalIgnoreCase))
                throw new XmlException();
        }
        catch (XmlException) { throw new RecoveryOperationException("The archive contains an invalid encryption-key XML document."); }
    }

    public static void EnsureDirectory(string directory)
    {
        // Reject links in every existing component, including a configured data-root link.
        for (var item = new DirectoryInfo(directory); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new RecoveryOperationException("The encryption-key directory must not contain symbolic links.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void CheckCollisions(string directory, IEnumerable<RecoveryKey> keys)
    {
        EnsureDirectory(directory);
        foreach (var key in keys)
        {
            var path = Path.Combine(directory, key.Name);
            if (!Path.Exists(path) && !File.Exists(path) && new FileInfo(path).LinkTarget is null) continue;
            ValidateFile(path);
            if (!ReadBounded(path).AsSpan().SequenceEqual(key.Content))
                throw new RecoveryOperationException("An encryption-key filename already exists with different contents. No keys were overwritten.");
        }
    }

    public static void Stage(string directory, IEnumerable<RecoveryKey> keys, List<string> created)
    {
        CheckCollisions(directory, keys);
        foreach (var key in keys)
        {
            var path = Path.Combine(directory, key.Name);
            if (File.Exists(path)) continue;
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(path, options);
            created.Add(path);
            file.Write(key.Content);
            file.Flush(true);
        }
    }

    private static void ValidateFile(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new RecoveryOperationException("Encryption-key entries must be regular files, not directories or symbolic links.");
    }

    private static byte[] ReadBounded(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumKeyBytes) throw new RecoveryOperationException("An encryption-key file exceeds the recovery limit.");
        using var output = new BoundedRecoveryStream(MaximumKeyBytes);
        file.CopyTo(output);
        return output.ToArray();
    }
}
