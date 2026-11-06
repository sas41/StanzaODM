namespace Stanza.Core.Automation;

public static class BashScriptRules
{
    public const int MaximumLength = 65536;

    public static bool IsValid(string? script) => !string.IsNullOrWhiteSpace(script) &&
        script.Length <= MaximumLength && !script.Contains('\0');

    public static bool IsServiceName(string? service) => service is { Length: > 0 and <= 100 } &&
        char.IsAsciiLetterOrDigit(service[0]) && service.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
