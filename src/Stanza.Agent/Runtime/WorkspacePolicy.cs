using System.Text.RegularExpressions;

namespace Stanza.Agent.Runtime;

public sealed partial class WorkspacePolicy
{
    public WorkspacePolicy(string persistentDataRoot)
    {
        Root = PrivateStorage.EnsureDirectory(Path.Combine(persistentDataRoot, "workspaces"));
    }

    public string Root { get; }

    public static bool IsIdentifier(string value) => value is not null && IdentifierPattern().IsMatch(value);

    public string GetWorkspace(string workspaceId)
    {
        if (!IsIdentifier(workspaceId)) { throw new ArgumentException("Invalid managed workspace identifier."); }
        var path = Path.Combine(Root, workspaceId);
        PrivateStorage.RejectLinks(path);
        if (!Directory.Exists(path)) { throw new IOException("The managed workspace is unavailable."); }
        return path;
    }

    public static string GetFile(string workspace, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\') ||
            relativePath.Split('/').Any(segment => segment is ".." or "." or "") || relativePath.Any(char.IsControl))
        {
            throw new ArgumentException("Invalid managed file path.");
        }

        var path = Path.GetFullPath(Path.Combine(workspace, relativePath));
        if (!path.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("File path is outside the managed workspace.");
        }

        PrivateStorage.RejectLinks(path);
        if (!File.Exists(path)) { throw new IOException("A required managed file is unavailable."); }
        return path;
    }

    // Applies to project, service and workspace identifiers; disallows option injection.
    [GeneratedRegex("\\A[a-z0-9][a-z0-9_-]{0,62}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
