using System.Security.Cryptography;
using Stanza.Agent;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class AgentIdentityTests
{
    [Fact]
    public void IdentityPersistsAndSignsFreshTokenBoundRequests()
    {
        using var directory = new AgentTestDirectory();
        using var first = AgentIdentity.LoadOrCreate(directory.Path);
        using var second = AgentIdentity.LoadOrCreate(directory.Path);
        Assert.Equal(first.AgentId, second.AgentId);
        Assert.Equal(first.PublicKey, second.PublicKey);
        var now = DateTimeOffset.UtcNow;
        var request = second.CreateEnrollmentRequest("secret-enrollment-token", "test-host", now);
        Assert.True(AgentEnrollmentProtocol.VerifyProof(request, now));
        Assert.NotEqual(request.Nonce, second.CreateEnrollmentRequest(request.Token, request.Hostname, now).Nonce);
        Assert.DoesNotContain(request.Token, request.ToString());
        Assert.DoesNotContain(request.Signature, request.ToString());
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { Token = "different-token" }, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { Hostname = "impersonator" }, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { AgentId = new string('a', 64) }, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { ProtocolVersion = 2 }, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request, now.AddMinutes(6)));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request, now.AddMinutes(-6)));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { Signature = new string('!', 88) }, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { Nonce = new string('!', 44) }, now));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(PrivateStorage.DirectoryMode, File.GetUnixFileMode(directory.Path));
            Assert.Equal(PrivateStorage.FileMode, File.GetUnixFileMode(Path.Combine(directory.Path, "identity.pk8")));
        }
    }

    [Fact]
    public void IndependentDirectoriesHaveDifferentIdentities()
    {
        using var firstDirectory = new AgentTestDirectory();
        using var secondDirectory = new AgentTestDirectory();
        using var first = AgentIdentity.LoadOrCreate(firstDirectory.Path);
        using var second = AgentIdentity.LoadOrCreate(secondDirectory.Path);
        Assert.NotEqual(first.AgentId, second.AgentId);
    }

    [Fact]
    public void CorruptIdentityIsNotSilentlyRegenerated()
    {
        using var directory = new AgentTestDirectory();
        using (AgentIdentity.LoadOrCreate(directory.Path)) { }
        File.WriteAllText(Path.Combine(directory.Path, "identity.pk8"), "invalid key");
        Assert.Throws<CryptographicException>(() => AgentIdentity.LoadOrCreate(directory.Path));
        Assert.Equal("invalid key", File.ReadAllText(Path.Combine(directory.Path, "identity.pk8")));
    }

    [Fact]
    public void IdentityRejectsSymlinkedFileAndDirectory()
    {
        using var directory = new AgentTestDirectory();
        using var target = new AgentTestDirectory();
        File.CreateSymbolicLink(Path.Combine(directory.Path, "identity.pk8"), Path.Combine(target.Path, "nonexistent"));
        Assert.Throws<IOException>(() => AgentIdentity.LoadOrCreate(directory.Path));
        Directory.CreateSymbolicLink(Path.Combine(target.Path, "linked"), directory.Path);
        Assert.Throws<IOException>(() => AgentIdentity.LoadOrCreate(Path.Combine(target.Path, "linked")));
    }

    [Fact]
    public void PubliclyReadableIdentityIsRejected()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var directory = new AgentTestDirectory();
        using (AgentIdentity.LoadOrCreate(directory.Path)) { }
        File.SetUnixFileMode(Path.Combine(directory.Path, "identity.pk8"), PrivateStorage.FileMode | UnixFileMode.OtherRead);
        Assert.Throws<IOException>(() => AgentIdentity.LoadOrCreate(directory.Path));
    }

    [Fact]
    public void OversizedIdentityFileIsRejectedBeforeImport()
    {
        using var directory = new AgentTestDirectory();
        using (AgentIdentity.LoadOrCreate(directory.Path)) { }
        File.WriteAllText(Path.Combine(directory.Path, "identity.pk8"), new string('x', 4097));
        Assert.Throws<IOException>(() => AgentIdentity.LoadOrCreate(directory.Path));
    }

    [Fact]
    public void ConcurrentCreationKeepsOneIdentity()
    {
        using var directory = new AgentTestDirectory();
        var identities = Enumerable.Range(0, 8).AsParallel().Select(_ => AgentIdentity.LoadOrCreate(directory.Path)).ToArray();
        try { Assert.Single(identities.Select(identity => identity.AgentId).Distinct()); }
        finally { foreach (var identity in identities) { identity.Dispose(); } }
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void PublicKeyDoesNotAllowSigningAsAnExistingAgent()
    {
        using var directory = new AgentTestDirectory();
        using var identity = AgentIdentity.LoadOrCreate(directory.Path);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var request = identity.CreateEnrollmentRequest("secret-token", "test-host", now);
        var forged = request with
        {
            Signature = Convert.ToBase64String(attacker.SignData(AgentEnrollmentProtocol.SigningPayload(request),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        };
        Assert.False(AgentEnrollmentProtocol.VerifyProof(forged, now));
    }

    [Fact]
    public void ProofValidationRejectsOversizedAndMissingFields()
    {
        using var directory = new AgentTestDirectory();
        using var identity = AgentIdentity.LoadOrCreate(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var request = identity.CreateEnrollmentRequest("secret-token", "test-host", now);
        Assert.False(AgentEnrollmentProtocol.VerifyProof(null, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { Token = new string('x', 4097) }, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { PublicKey = null! }, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { PublicKey = new string('x', 513) }, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { Hostname = "host\nmalicious" }, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { Hostname = null! }, now));
        Assert.False(AgentEnrollmentProtocol.VerifyProof(request with { Hostname = new string('x', 254) }, now));
    }
}
