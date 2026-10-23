namespace Stanza.Agent.Runtime;

/// <summary>Retains enough tail to recognize credentials split across process reads.</summary>
public sealed class OutputRedactor(IEnumerable<string> values)
{
    private readonly string[] secrets = values.Where(x => x.Length > 0).Distinct().OrderByDescending(x => x.Length).ToArray();
    private string pending = "";

    public string Append(string text)
    {
        pending += text;
        var safe = Math.Max(0, pending.Length - (secrets.Select(x => x.Length).DefaultIfEmpty(1).Max() - 1));
        // Do not cut through a secret; release it atomically as a replacement.
        foreach (var secret in secrets)
        {
            var index = pending.IndexOf(secret, StringComparison.Ordinal);
            while (index >= 0)
            {
                if (index < safe && index + secret.Length > safe) safe = index;
                index = pending.IndexOf(secret, index + secret.Length, StringComparison.Ordinal);
            }
        }
        if (safe > 0 && safe < pending.Length && char.IsHighSurrogate(pending[safe - 1])) safe--;
        var result = Redact(pending[..safe]);
        pending = pending[safe..];
        return result;
    }

    public string Flush() { var result = Redact(pending); pending = ""; return result; }
    private string Redact(string text)
    {
        foreach (var secret in secrets) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }
}
