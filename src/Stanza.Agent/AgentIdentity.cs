using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Stanza.Core.Contracts;

namespace Stanza.Agent;

public sealed class AgentIdentity : IDisposable
{
    private readonly ECDsa key;
    private readonly object gate = new();

    private AgentIdentity(ECDsa key)
    {
        this.key = key;
        var publicKey = key.ExportSubjectPublicKeyInfo();
        PublicKey = Convert.ToBase64String(publicKey);
        AgentId = AgentEnrollmentProtocol.GetAgentId(publicKey);
    }

    public string AgentId { get; }
    public string PublicKey { get; }

    public static AgentIdentity LoadOrCreate(string persistentDataRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Agent identity storage requires Unix filesystem permissions.");
        }

        var directory = PrivateStorage.EnsureDirectory(persistentDataRoot);
        var path = Path.Combine(directory, "identity.pk8");
        PrivateStorage.RejectLinks(path);
        if (!File.Exists(path))
        {
            using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var bytes = generated.ExportPkcs8PrivateKey();
            var temporary = Path.Combine(directory, $".identity-{Guid.NewGuid():N}");
            try
            {
                using (var stream = new FileStream(temporary, new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = System.IO.FileMode.CreateNew,
                    Share = FileShare.None,
                    UnixCreateMode = PrivateStorage.FileMode
                }))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                }

                try { File.Move(temporary, path, false); }
                catch (IOException) when (File.Exists(path)) { /* Another startup won creation. */ }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                File.Delete(temporary);
            }
        }

        PrivateStorage.RejectLinks(path);
        if ((File.GetUnixFileMode(path) & ~PrivateStorage.FileMode) != 0 || new FileInfo(path).Length > 4096)
        {
            throw new IOException("Agent identity file must be private and contain a valid identity.");
        }

        var privateKey = File.ReadAllBytes(path);
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            if (bytesRead != privateKey.Length || key.KeySize != 256 ||
                key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new CryptographicException("Agent identity must be a P-256 key.");
            }

            return new AgentIdentity(key);
        }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    public AgentEnrollmentRequest CreateEnrollmentRequest(string token, string hostname, DateTimeOffset now)
    {
        var request = new AgentEnrollmentRequest(token, AgentEnrollmentProtocol.Name, AgentEnrollmentProtocol.Version,
            AgentId, PublicKey, hostname, now.ToUnixTimeSeconds(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "");
        lock (gate)
        {
            return request with
            {
                Signature = Convert.ToBase64String(key.SignData(AgentEnrollmentProtocol.SigningPayload(request),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            };
        }
    }

    public void Dispose() => key.Dispose();
}
