using System.Text;
using Stanza.ControlPlane.Deployments;

namespace Stanza.ControlPlane.Editing;

public sealed record RevisionDiffLine(string Kind, string Text);
public sealed record DeploymentFileDiff(string Path, string Status, IReadOnlyList<RevisionDiffLine> Lines);
public sealed record DeploymentRevisionDiff(Guid DeploymentId, long Revision, long LatestRevision, IReadOnlyList<DeploymentFileDiff> Files);

/// <summary>A bounded, linear-memory line diff. Unique-line anchors preserve ordering; gaps are explicit replacement hunks.</summary>
public static class RevisionDiff
{
    public static IReadOnlyList<RevisionDiffLine> Compare(string before, string after)
    {
        if (Encoding.UTF8.GetByteCount(before) > DeploymentDraftService.MaximumTextBytes || Encoding.UTF8.GetByteCount(after) > DeploymentDraftService.MaximumTextBytes)
            throw new DeploymentDraftException("Text diffs are limited to 1 MiB per file; download exact files to compare larger content.");
        var a = Lines(before); var b = Lines(after);
        var result = new List<RevisionDiffLine>();
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) { result.Add(new("context", a[start])); start++; }
        var ae = a.Length; var be = b.Length;
        while (ae > start && be > start && a[ae - 1] == b[be - 1]) { ae--; be--; }
        // A greedy increasing set of unique anchors is deterministic and cannot consume quadratic memory/time.
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var j = start; j < be; j++) positions[b[j]] = positions.ContainsKey(b[j]) ? -1 : j;
        var ai = start; var bi = start;
        for (var i = start; i < ae; i++)
        {
            if (!positions.TryGetValue(a[i], out var j) || j < bi) continue;
            while (ai < i) result.Add(new("removed", a[ai++]));
            while (bi < j) result.Add(new("added", b[bi++]));
            result.Add(new("context", a[ai++])); bi++;
        }
        while (ai < ae) result.Add(new("removed", a[ai++]));
        while (bi < be) result.Add(new("added", b[bi++]));
        while (ae < a.Length) result.Add(new("context", a[ae++]));
        return result;
    }

    private static string[] Lines(string value)
    {
        var lines = new List<string>(); var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\r' && value[i] != '\n') continue;
            if (value[i] == '\r' && i + 1 < value.Length && value[i + 1] == '\n') i++;
            lines.Add(value[start..(i + 1)]); start = i + 1;
        }
        if (start < value.Length) lines.Add(value[start..]);
        return lines.ToArray();
    }
}
