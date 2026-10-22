using System.Text.Json;

namespace Stanza.ControlPlane.Portability;

public static class PortableMappingParser
{
    public static IReadOnlyDictionary<string, T> Parse<T>(string? input)
    {
        input = string.IsNullOrWhiteSpace(input) ? "{}" : input;
        if (input.Length > 131072) throw new PortabilityException("Mapping input exceeds 128 KiB.");
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new PortabilityException("Mappings must be JSON objects.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Length > 100 || !keys.Add(property.Name)) throw new PortabilityException("Mappings contain duplicate or oversized logical keys.");
            var values = JsonSerializer.Deserialize<Dictionary<string, T>>(input, PortableBundleValidator.JsonOptions)
                ?? throw new PortabilityException("Mappings must be JSON objects.");
            if (values.Count > 500 || values.Any(x => x.Value is null)) throw new PortabilityException("Mappings exceed reference limits or contain null values.");
            return values;
        }
        catch (JsonException) { throw new PortabilityException("Mappings must be JSON objects from logical names to destination IDs or names."); }
    }
}
