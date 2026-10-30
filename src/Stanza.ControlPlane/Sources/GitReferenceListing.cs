using System.Text.Json;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Sources;

public static class GitReferenceListing
{
    public static IReadOnlyList<GitReference> Read(AgentWorkResponse response)
    {
        if (response.Output is null || response.Output.Length > 4 * 1024 * 1024) throw new SourceException("Invalid or oversized reference listing.");
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(response.Output) ?? [];
            if (names.Length > 10000) throw new SourceException("The remote has too many references.");
            foreach (var name in names) SourceValidation.Reference(name);
            var metadata = response.GitReferences ?? [];
            if (metadata.Count > 10000 || metadata.Any(x => x is null || x.Name is null ||
                x.Commit is { } hash && (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) ||
                x.CommittedAt is < -62135596800 or > 253402300799 || x.Author?.Length > 256))
                throw new SourceException("Invalid reference metadata.");
            var details = metadata.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            // Older Agents still return names only. Preserve upgrade compatibility until Agents update.
            return names.Distinct(StringComparer.Ordinal).Select(x => details.GetValueOrDefault(x) ?? new(x))
                .OrderByDescending(x => x.CommittedAt).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { throw new SourceException("Invalid reference listing."); }
    }
}
