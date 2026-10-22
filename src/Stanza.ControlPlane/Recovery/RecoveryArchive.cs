using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stanza.ControlPlane.Recovery;

public sealed class RecoveryOperationException(string message) : Exception(message);

// Deliberately not records: generated ToString implementations must never expose archive secrets.
internal sealed class RecoveryArchive
{
    [JsonRequired] public int FormatVersion { get; set; } = 1;
    [JsonRequired] public string Application { get; set; } = "Stanza.ControlPlane";
    [JsonRequired] public string Provider { get; set; } = "";
    [JsonRequired] public string SchemaFingerprint { get; set; } = "";
    [JsonRequired] public DateTimeOffset CreatedAt { get; set; }
    [JsonRequired] public List<RecoveryTable> Tables { get; set; } = [];
    [JsonRequired] public List<RecoveryKey> Keys { get; set; } = [];
}

internal sealed class RecoveryTable
{
    public string Name { get; set; } = "";
    public string? Schema { get; set; }
    public List<RecoveryColumn> Columns { get; set; } = [];
    public List<RecoveryCell[]> Rows { get; set; } = [];
}

internal sealed class RecoveryColumn
{
    public string Name { get; set; } = "";
    public string StoreType { get; set; } = "";
    public bool Nullable { get; set; }
}

internal sealed class RecoveryKey
{
    public string Name { get; set; } = "";
    public byte[] Content { get; set; } = [];
}

internal sealed class RecoveryCell
{
    public string Type { get; set; } = "null";
    public string? Value { get; set; }

    public static RecoveryCell From(object value) => value switch
    {
        DBNull => new(),
        bool v => new() { Type = "bool", Value = v ? "true" : "false" },
        short v => new() { Type = "short", Value = v.ToString(CultureInfo.InvariantCulture) },
        int v => new() { Type = "int", Value = v.ToString(CultureInfo.InvariantCulture) },
        long v => new() { Type = "long", Value = v.ToString(CultureInfo.InvariantCulture) },
        float v when float.IsFinite(v) => new() { Type = "float", Value = v.ToString("R", CultureInfo.InvariantCulture) },
        double v when double.IsFinite(v) => new() { Type = "double", Value = v.ToString("R", CultureInfo.InvariantCulture) },
        decimal v => new() { Type = "decimal", Value = v.ToString(CultureInfo.InvariantCulture) },
        Guid v => new() { Type = "guid", Value = v.ToString("D") },
        DateTime v when v.Kind == DateTimeKind.Utc => new() { Type = "utc", Value = v.ToString("O", CultureInfo.InvariantCulture) },
        DateTimeOffset v => new() { Type = "offset", Value = v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) },
        string v => new() { Type = "string", Value = v },
        byte[] v => new() { Type = "bytes", Value = Convert.ToBase64String(v) },
        _ => throw new RecoveryOperationException("A database value has an unsupported recovery type; no backup was produced.")
    };

    public object Decode() => Type switch
    {
        "null" when Value is null => DBNull.Value,
        "bool" when Value is "true" or "false" => Value == "true",
        "short" => short.Parse(Value!, CultureInfo.InvariantCulture),
        "int" => int.Parse(Value!, CultureInfo.InvariantCulture),
        "long" => long.Parse(Value!, CultureInfo.InvariantCulture),
        "float" => Finite(float.Parse(Value!, CultureInfo.InvariantCulture)),
        "double" => Finite(double.Parse(Value!, CultureInfo.InvariantCulture)),
        "decimal" => decimal.Parse(Value!, CultureInfo.InvariantCulture),
        "guid" => Guid.ParseExact(Value!, "D"),
        "utc" => Utc(Value!),
        "offset" => DateTimeOffset.ParseExact(Value!, "O", CultureInfo.InvariantCulture),
        "string" when Value is not null => Value,
        "bytes" => Convert.FromBase64String(Value!),
        _ => throw new RecoveryOperationException("The recovery archive contains an invalid value type.")
    };

    private static float Finite(float value) => float.IsFinite(value) ? value : throw new FormatException();
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new FormatException();
    private static DateTime Utc(string value)
    {
        var result = DateTime.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return result.Kind == DateTimeKind.Utc ? result : throw new FormatException();
    }
}

internal static class RecoveryEncryption
{
    private static readonly byte[] Magic = "STANZA-R1"u8.ToArray();
    private const int SaltLength = 32, NonceLength = 12, TagLength = 16, Iterations = 600_000;
    private const int HeaderLength = 9 + SaltLength + NonceLength + TagLength;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32, PropertyNameCaseInsensitive = false };

    public static byte[] Encrypt(RecoveryArchive archive, string passphrase)
    {
        ValidatePassphrase(passphrase);
        using var stream = new BoundedRecoveryStream(RecoveryService.MaximumArchiveBytes - HeaderLength);
        JsonSerializer.Serialize(stream, archive, JsonOptions);
        var plaintext = stream.ToArray();
        var output = new byte[HeaderLength + plaintext.Length];
        Magic.CopyTo(output, 0);
        RandomNumberGenerator.Fill(output.AsSpan(9, SaltLength));
        RandomNumberGenerator.Fill(output.AsSpan(9 + SaltLength, NonceLength));
        var key = Derive(passphrase, output.AsSpan(9, SaltLength));
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(output.AsSpan(9 + SaltLength, NonceLength), plaintext,
                output.AsSpan(HeaderLength), output.AsSpan(HeaderLength - TagLength, TagLength), output.AsSpan(0, HeaderLength - TagLength));
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static RecoveryArchive Decrypt(byte[] encrypted, string passphrase)
    {
        ValidatePassphrase(passphrase);
        if (encrypted.Length <= HeaderLength || encrypted.Length > RecoveryService.MaximumArchiveBytes || !encrypted.AsSpan(0, 9).SequenceEqual(Magic))
            throw new RecoveryOperationException("Invalid, truncated, oversized, or unsupported recovery archive.");
        var plaintext = new byte[encrypted.Length - HeaderLength];
        var key = Derive(passphrase, encrypted.AsSpan(9, SaltLength));
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(encrypted.AsSpan(9 + SaltLength, NonceLength), encrypted.AsSpan(HeaderLength),
                encrypted.AsSpan(HeaderLength - TagLength, TagLength), plaintext, encrypted.AsSpan(0, HeaderLength - TagLength));
            Preflight(plaintext);
            return JsonSerializer.Deserialize<RecoveryArchive>(plaintext, JsonOptions)
                ?? throw new RecoveryOperationException("Invalid recovery archive.");
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            throw new RecoveryOperationException("The passphrase is incorrect or the recovery archive is damaged.");
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length is < 16 or > 1024)
            throw new RecoveryOperationException("Use a recovery passphrase of 16–1024 characters. It cannot be recovered if lost.");
    }

    private static byte[] Derive(string passphrase, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static void Preflight(ReadOnlySpan<byte> plaintext)
    {
        // Check collection bounds before allocating object graphs. The encrypted byte limit
        // alone would still allow millions of tiny JSON arrays/nulls to exhaust memory.
        var reader = new Utf8JsonReader(plaintext, new JsonReaderOptions { MaxDepth = 32 });
        var arrays = new Stack<(string Kind, int Depth)>();
        string? property = null;
        int tokens = 0, rows = 0, cells = 0, tables = 0, columns = 0, keys = 0;
        while (reader.Read())
        {
            if (++tokens > 8_000_000) throw new RecoveryOperationException("The archive exceeds the recovery JSON complexity limit.");
            if (arrays.TryPeek(out var parent) && reader.CurrentDepth == parent.Depth + 1 && reader.TokenType is not JsonTokenType.PropertyName and not JsonTokenType.EndArray and not JsonTokenType.EndObject)
            {
                var exceeded = parent.Kind switch
                {
                    "Tables" => ++tables > 256,
                    "Columns" => ++columns > 256 * 512,
                    "Rows" => ++rows > 100_000,
                    "Row" => ++cells > 1_000_000,
                    "Keys" => ++keys > 1024,
                    _ => false
                };
                if (exceeded) throw new RecoveryOperationException("The archive exceeds the bounded recovery entry counts.");
            }
            if (reader.TokenType == JsonTokenType.PropertyName) property = reader.GetString();
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                var kind = property is "Tables" or "Columns" or "Rows" or "Keys" ? property : arrays.TryPeek(out var enclosing) && enclosing.Kind == "Rows" ? "Row" : "Other";
                arrays.Push((kind, reader.CurrentDepth));
                property = null;
            }
            else if (reader.TokenType == JsonTokenType.EndArray) arrays.Pop();
            else property = null;
        }
    }
}

internal sealed class BoundedRecoveryStream(long maximum) : MemoryStream
{
    private void Check(int count)
    {
        if (Position + count > maximum) throw new RecoveryOperationException("This instance exceeds the 128 MiB recovery archive limit.");
    }
    public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { Check(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
    public override void SetLength(long value)
    {
        if (value > maximum) throw new RecoveryOperationException("The recovery archive exceeds the maximum size.");
        base.SetLength(value);
    }
}
