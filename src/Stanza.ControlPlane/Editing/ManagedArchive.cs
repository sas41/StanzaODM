using System.IO.Compression;
using System.Text;
using Stanza.ControlPlane.Deployments;
using Stanza.Core.Deployments;

namespace Stanza.ControlPlane.Editing;

/// <summary>Extracts only into immutable virtual entries. No archive path ever reaches the host filesystem.</summary>
public static class ManagedArchive
{
    public static IReadOnlyList<ManagedFileSnapshot> ReadZip(byte[] bytes, string destination, DeploymentEditorLimits limits)
    {
        if (bytes.Length > limits.UploadBytes) throw new DeploymentDraftException("Archive exceeds the configured upload limit.");
        try
        {
            var prefix = string.IsNullOrEmpty(destination) ? "" : DeploymentPath.Parse(destination).Value + "/";
            using var input = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count > limits.SupportingEntries) throw new DeploymentDraftException("Archive exceeds the configured entry limit.");
            var files = new List<ManagedFileSnapshot>();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var mode = (entry.ExternalAttributes >> 16) & 0xf000;
                var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                if ((mode != 0 && mode != (directory ? 0x4000 : 0x8000)) || (entry.ExternalAttributes & 0x400) != 0)
                    throw new DeploymentDraftException("Archives may contain only regular files and directories, not links or special entries.");
                var path = prefix + (directory ? entry.FullName[..^1] : entry.FullName);
                _ = DeploymentPath.Parse(path);
                if (entry.Length > limits.UploadBytes || entry.Length > limits.WorkspaceBytes - total || (directory && entry.Length != 0))
                    throw new DeploymentDraftException("Expanded archive exceeds the configured byte limits.");
                if (directory) { files.Add(ManagedFileSnapshot.Directory(path)); continue; }
                using var source = entry.Open();
                using var content = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer)) != 0)
                {
                    if (content.Length + read > entry.Length || content.Length + read > limits.UploadBytes || total + read > limits.WorkspaceBytes)
                        throw new DeploymentDraftException("Expanded archive exceeds the configured byte limits.");
                    content.Write(buffer, 0, read);
                    total += read;
                }
                if (content.Length != entry.Length) throw new DeploymentDraftException("Archive entry length is invalid.");
                var bytesRead = content.ToArray();
                var file = ManagedFileSnapshot.Binary(path, bytesRead);
                if (bytesRead.Length <= limits.TextBytes && !bytesRead.Contains((byte)0))
                {
                    try { file = ManagedFileSnapshot.Utf8Text(path, bytesRead); }
                    catch (DecoderFallbackException) { /* Non-UTF-8 assets remain exact binary files. */ }
                }
                files.Add(file);
            }
            DeploymentWorkspaceValidator.Validate(files, [], new(limits.SupportingEntries, limits.UploadBytes, limits.WorkspaceBytes));
            return files;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException or NotSupportedException)
        { throw new DeploymentDraftException("Invalid ZIP archive, unsafe path, conflicting paths, or unsupported archive encoding."); }
    }
}
