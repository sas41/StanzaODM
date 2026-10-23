using System.Text;

namespace Stanza.Core.Deployments;

public enum ManagedEntryKind { Text, Binary, Directory }

/// <summary>Immutable regular-file bytes or an explicit directory. Symlinks are deliberately unsupported.</summary>
public sealed class ManagedFileSnapshot
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] content;

    private ManagedFileSnapshot(DeploymentPath path, ManagedEntryKind kind, byte[] content)
    {
        Path = path;
        Kind = kind;
        this.content = content;
    }

    public DeploymentPath Path { get; }
    public ManagedEntryKind Kind { get; }
    public long Length => content.LongLength;

    public static ManagedFileSnapshot Text(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(DeploymentPath.Parse(path), ManagedEntryKind.Text, StrictUtf8.GetBytes(text));
    }

    public static ManagedFileSnapshot Binary(string path, ReadOnlySpan<byte> bytes) =>
        new(DeploymentPath.Parse(path), ManagedEntryKind.Binary, bytes.ToArray());

    /// <summary>Accepts only valid UTF-8 and preserves every original byte, including any BOM.</summary>
    public static ManagedFileSnapshot Utf8Text(string path, ReadOnlySpan<byte> bytes)
    {
        var validatedPath = DeploymentPath.Parse(path);
        var copy = bytes.ToArray();
        _ = StrictUtf8.GetCharCount(copy);
        return new(validatedPath, ManagedEntryKind.Text, copy);
    }

    public static ManagedFileSnapshot Directory(string path) =>
        new(DeploymentPath.Parse(path), ManagedEntryKind.Directory, []);

    /// <summary>Returns a defensive copy, including any BOM and original line endings.</summary>
    public byte[] GetBytes() => (byte[])content.Clone();

    public string GetText() => Kind == ManagedEntryKind.Text
        ? StrictUtf8.GetString(content)
        : throw new InvalidOperationException("This entry is not an editable text file.");

    public ManagedFileSnapshot Rename(string path) => new(DeploymentPath.Parse(path), Kind, content);

    public override string ToString() => $"{Path} ({Kind}, {Length} bytes)";
}

/// <summary>Limits are supplied by application settings, not hard-coded upload policy.</summary>
public sealed record ManagedFileLimits
{
    public ManagedFileLimits(int maximumEntries, long maximumFileBytes, long maximumTotalBytes)
    {
        if (maximumEntries < 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumFileBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        if (maximumTotalBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes));
        MaximumEntries = maximumEntries;
        MaximumFileBytes = maximumFileBytes;
        MaximumTotalBytes = maximumTotalBytes;
    }

    public int MaximumEntries { get; }
    public long MaximumFileBytes { get; }
    public long MaximumTotalBytes { get; }
}

/// <summary>Reference to an externally managed Git source; no checkout or remote access occurs here.</summary>
public sealed record GitSourceWorkspace
{
    public GitSourceWorkspace(Guid sourceId, string destination)
    {
        if (sourceId == Guid.Empty) throw new ArgumentException("A source identity is required.", nameof(sourceId));
        SourceId = sourceId;
        Destination = DeploymentPath.Parse(destination);
    }

    public Guid SourceId { get; }
    public DeploymentPath Destination { get; }
}

public static class DeploymentWorkspaceValidator
{
    public static void Validate(IReadOnlyList<ManagedFileSnapshot> files,
        IReadOnlyList<GitSourceWorkspace> sources, ManagedFileLimits limits)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumEntries > 0 && files.Count > limits.MaximumEntries)
            throw new ArgumentException("Managed entry count exceeds the configured limit.", nameof(files));

        long total = 0;
        var entries = new Dictionary<string, ManagedFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        // Include implicit directory names so Foo/a and foo/b cannot create a case collision.
        var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (limits.MaximumFileBytes > 0 && file.Length > limits.MaximumFileBytes || limits.MaximumTotalBytes > 0 && file.Length > limits.MaximumTotalBytes - total)
                throw new ArgumentException("Managed file bytes exceed the configured limit.", nameof(files));
            total += file.Length;
            if (!entries.TryAdd(file.Path.Value, file))
                throw new ArgumentException("Duplicate or case-colliding managed paths.", nameof(files));
            RegisterSpelling(file.Path, spelling);
        }

        foreach (var file in files)
        {
            var path = file.Path.Value;
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (entries.TryGetValue(path[..slash], out var ancestor) && ancestor.Kind != ManagedEntryKind.Directory)
                    throw new ArgumentException("A regular file cannot contain other entries.", nameof(files));
        }

        var sourceIds = new HashSet<Guid>();
        var destinations = new List<DeploymentPath>();
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!sourceIds.Add(source.SourceId))
                throw new ArgumentException("Duplicate Git source identity.", nameof(sources));
            RegisterSpelling(source.Destination, spelling);
            foreach (var other in destinations)
                if (Overlaps(source.Destination, other))
                    throw new ArgumentException("Git destinations must not overlap.", nameof(sources));
            foreach (var file in files)
                if (source.Destination.IsSameOrAncestorOf(file.Path) ||
                    (file.Kind != ManagedEntryKind.Directory && file.Path.IsSameOrAncestorOf(source.Destination)))
                    throw new ArgumentException("Git destination collides with managed files.", nameof(sources));
            destinations.Add(source.Destination);
        }
    }

    private static bool Overlaps(DeploymentPath left, DeploymentPath right) =>
        left.IsSameOrAncestorOf(right) || right.IsSameOrAncestorOf(left);

    private static void RegisterSpelling(DeploymentPath path, Dictionary<string, string> spelling)
    {
        var segments = path.Value.Split('/');
        for (var length = 1; length <= segments.Length; length++)
        {
            var prefix = string.Join('/', segments.Take(length));
            if (spelling.TryGetValue(prefix, out var existing) && !existing.Equals(prefix, StringComparison.Ordinal))
                throw new ArgumentException("Workspace paths have conflicting case spellings.", nameof(path));
            spelling[prefix] = prefix;
        }
    }
}
