using System.Text.Json;
using System.Text.Json.Serialization;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Terminals;

public static class TerminalProtocol
{
    public const int MaximumInputBytes = 65536;
    public const int MaximumFrameBytes = 90 * 1024;
    public const int MaximumOpenBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static TerminalMessage Parse(ReadOnlyMemory<byte> data) =>
        JsonSerializer.Deserialize<TerminalMessage>(data.Span, Json) ?? throw new JsonException();

    public static AgentExecution Execution(TerminalMessage message)
    {
        if (message.Type != "open" || message.Data is not null || !ValidDimensions(message.Columns, message.Rows)
            || !SafeTarget(message.Service) || !SafeTarget(message.Container)
            || (string.IsNullOrWhiteSpace(message.Service) == string.IsNullOrWhiteSpace(message.Container))
            || !Safe(message.Executable, 1024) || string.IsNullOrWhiteSpace(message.Executable)
            || message.Executable.StartsWith('-') || !Safe(message.User, 128) || !Safe(message.WorkingDirectory, 1024)
            || message.Arguments is null || message.Arguments.Count > 64
            || message.Arguments.Any(argument => argument is null || argument.Length > 4096 || argument.Contains('\0'))
            || message.Arguments.Sum(argument => argument.Length) > 8192)
            throw new TerminalProtocolException();
        if (message.Service?.StartsWith('-') == true || message.Container?.StartsWith('-') == true
            || message.User?.StartsWith('-') == true
            || message.WorkingDirectory is { Length: > 0 } directory && !directory.StartsWith('/'))
            throw new TerminalProtocolException();
        return new(message.Executable, message.Arguments, Empty(message.Service), Empty(message.Container),
            Empty(message.User), Empty(message.WorkingDirectory));
    }

    public static byte[] Input(TerminalMessage message)
    {
        if (message.Type != "input" || message.Data is null || message.Data.Length > 87384) throw new TerminalProtocolException();
        var bytes = Convert.FromBase64String(message.Data);
        if (bytes.Length is 0 or > MaximumInputBytes) throw new TerminalProtocolException();
        return bytes;
    }

    public static bool ValidDimensions(int columns, int rows) => columns is >= 2 and <= 500 && rows is >= 1 and <= 300;
    private static bool Safe(string? value, int maximum) => value is null || value.Length <= maximum && !value.Any(char.IsControl);
    private static bool SafeTarget(string? value) => value is null or "" || value.Length <= 128
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed class TerminalProtocolException : Exception;

public sealed record TerminalMessage(string Type, string? Service = null, string? Container = null,
    string? Executable = null, IReadOnlyList<string>? Arguments = null, string? User = null,
    string? WorkingDirectory = null, int Columns = 0, int Rows = 0, string? Data = null)
{
    public override string ToString() => $"TerminalMessage {{ Type = {Type}, Contents = [REDACTED] }}";
}
