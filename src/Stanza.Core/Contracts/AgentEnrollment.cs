using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stanza.Core.Contracts;

public sealed record AgentEnrollmentRequest(
    string Token,
    string Protocol,
    int ProtocolVersion,
    string AgentId,
    string PublicKey,
    string Hostname,
    long IssuedAtUnixSeconds,
    string Nonce,
    string Signature)
{
    public override string ToString() => $"AgentEnrollmentRequest {{ AgentId = {AgentId}, ProtocolVersion = {ProtocolVersion} }}";
}

public sealed record AgentEnrollmentResponse(int ProtocolVersion, string Status);

public static class AgentEnrollmentProtocol
{
    public const string Name = "stanza-agent-enrollment";
    public const int Version = 1;
    public const string Endpoint = "/api/agent-enrollment";
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Revoked = "revoked";

    // Sign the UTF-8 JSON string array, not the serialized HTTP object. The token hash
    // binds this proof to this enrollment secret without placing the token in the proof.
    public static byte[] SigningPayload(AgentEnrollmentRequest request) => JsonSerializer.SerializeToUtf8Bytes(new[]
    {
        request.Protocol,
        request.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
        request.AgentId,
        request.PublicKey,
        request.Hostname,
        request.IssuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture),
        request.Nonce,
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Token)))
    });

    public static string GetAgentId(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));

    // The receiver MUST additionally validate/consume the enrollment token, atomically
    // reject previously seen (AgentId, Nonce) pairs, and enforce approval separately.
    public static bool VerifyProof(AgentEnrollmentRequest? request, DateTimeOffset now)
    {
        if (request is null || request.Protocol != Name || request.ProtocolVersion != Version ||
            string.IsNullOrEmpty(request.Token) || request.Token.Length > 4096 ||
            request.AgentId?.Length != 64 || request.PublicKey?.Length is not (> 0 and <= 512) ||
            request.Hostname?.Length is not (> 0 and <= 253) || request.Hostname.Any(char.IsControl) ||
            request.Nonce?.Length != 44 || request.Signature?.Length != 88 ||
            request.IssuedAtUnixSeconds < now.AddMinutes(-5).ToUnixTimeSeconds() ||
            request.IssuedAtUnixSeconds > now.AddMinutes(5).ToUnixTimeSeconds())
        {
            return false;
        }

        try
        {
            var publicKey = Convert.FromBase64String(request.PublicKey);
            if (GetAgentId(publicKey) != request.AgentId || Convert.FromBase64String(request.Nonce).Length != 32)
            {
                return false;
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            return bytesRead == publicKey.Length && key.KeySize == 256 &&
                key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value &&
                key.VerifyData(SigningPayload(request), Convert.FromBase64String(request.Signature),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
