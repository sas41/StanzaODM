namespace Stanza.Core.Automation;

public static class BashScriptRules
{
    public const int MaximumLength = 65536;

    public static bool IsValid(string? script) => !string.IsNullOrWhiteSpace(script) &&
        script.Length <= MaximumLength && !script.Contains('\0');

    public static bool IsServiceName(string? service) => service is { Length: > 0 and <= 100 } &&
        char.IsAsciiLetterOrDigit(service[0]) && service.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    public static bool IsVariableName(string? name) => name is { Length: > 0 and <= 64 } &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
        !name.StartsWith("STANZA_", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("BASH", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("LD_", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("DOCKER_", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) &&
        !new[] { "ENV", "PATH", "HOME", "SHELLOPTS", "SHELL", "IFS", "CDPATH", "GLOBIGNORE", "PROMPT_COMMAND", "PS4" }.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsEnvironment(IReadOnlyDictionary<string, string>? environment) => environment is null ||
        environment.Count <= 50 && environment.All(p => IsVariableName(p.Key) && p.Value is not null && p.Value.Length <= 4096 && !p.Value.Contains('\0'));

    public static bool IsTarget(string kind, Contracts.AgentExecution execution) => kind == "container-script"
        ? (execution.Services is null ? IsServiceName(execution.Service) : execution.Service is null &&
            execution.Services.Count is > 0 and <= 100 && execution.Services.All(IsServiceName) &&
            execution.Services.Distinct(StringComparer.Ordinal).Count() == execution.Services.Count) && execution.Replicas is null or "once" or "all"
        : execution.Service is null && execution.Services is null && execution.Replicas is null;
}
