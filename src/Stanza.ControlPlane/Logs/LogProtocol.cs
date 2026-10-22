using System.Text.Json;
using System.Text.Json.Serialization;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Logs;

public static class LogProtocol
{
    public const int MaximumClientMessageBytes = 2048;
    public const int MaximumOutputBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    public static LogMessage Parse(ReadOnlyMemory<byte> bytes) =>
        JsonSerializer.Deserialize<LogMessage>(bytes.Span, Json) ?? throw new JsonException();

    public static AgentExecution Execution(LogMessage message)
    {
        if (message.Type != "open" || !ValidTarget(message.Service) || !ValidTarget(message.Container)
            || (string.IsNullOrEmpty(message.Service) == string.IsNullOrEmpty(message.Container)))
            throw new LogProtocolException();
        // Logs mode does not execute a program. No user-supplied executable, args, environment,
        // user or working directory can enter the Agent request through this read-only protocol.
        return new("logs", [], Service: Empty(message.Service), Container: Empty(message.Container), Mode: "logs");
    }

    public static bool IsClose(LogMessage message) => message.Type == "close" && message.Service is null && message.Container is null;
    private static bool ValidTarget(string? value) => value is null or "" || value.Length <= 128
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
    private static string? Empty(string? value) => value is "" ? null : value;
}

public sealed record LogMessage(string Type, string? Service = null, string? Container = null)
{
    public override string ToString() => "LogMessage { Contents = [REDACTED] }";
}

public sealed class LogProtocolException : Exception;
