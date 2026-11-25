using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Palladin.Core.Cryptography;
using Palladin.Module.Agents.Infrastructure.Crypto;

namespace Palladin.Tests.Unit.Modules.Agents;

public sealed class AgentEncryptionKeyRotationTests
{
    private static readonly Guid AgentId = Guid.Parse("0f0e0d0c-0b0a-4908-8706-050403020100");

    [Fact]
    public void When_AgentSignsTheRotationStatement_Then_BindingVerifiesForExactlyThoseKeys()
    {
        // Given
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        var previous = AgentEncryptionKeys.Fingerprint(AgentEncryptionKeys.EnrollmentSuite.Id, RandomNumberGenerator.GetBytes(32));
        var next = AgentEncryptionKeys.Fingerprint(
            KeyWrapperSuites.Current.Id, RandomNumberGenerator.GetBytes(KeyWrapperSuites.Current.PublicKeyBytes));
        var signature = SignatureAlgorithm.Ed25519.Sign(signingKey, Statement(previous, KeyWrapperSuites.Current.Id, next));
        var publicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        // When / Then
        AgentEncryptionKeys.VerifyRotation(publicKey, AgentId, previous, KeyWrapperSuites.Current.Id, next, signature)
            .ShouldBeTrue();
        AgentEncryptionKeys.VerifyRotation(publicKey, Guid.NewGuid(), previous, KeyWrapperSuites.Current.Id, next, signature)
            .ShouldBeFalse();
        AgentEncryptionKeys.VerifyRotation(publicKey, AgentId, next, KeyWrapperSuites.Current.Id, previous, signature)
            .ShouldBeFalse();
        AgentEncryptionKeys.VerifyRotation(publicKey, AgentId, previous, AgentEncryptionKeys.EnrollmentSuite.Id, next, signature)
            .ShouldBeFalse();
    }

    [Fact]
    public void When_FingerprintsUseTheirSuites_Then_SuiteBoundAndLegacyFormulasDiffer()
    {
        // Given
        var key = new byte[32];

        // When
        var legacy = AgentEncryptionKeys.Fingerprint(AgentEncryptionKeys.EnrollmentSuite.Id, key);

        // Then
        legacy.ShouldBe(SHA256.HashData([.. "PLDNV2FP"u8, 0, 2, 0, 1, .. key]));
        Should.Throw<Palladin.Core.Types.Exceptions.DomainException>(() =>
            AgentEncryptionKeys.Fingerprint(KeyWrapperSuites.Current.Id, key));
    }

    // Independent re-statement of palladin-agent identity.rs encryption_key_rotation_statement.
    private static byte[] Statement(byte[] previous, string suiteId, byte[] next)
    {
        var suite = Encoding.ASCII.GetBytes(suiteId);
        using var stream = new MemoryStream();
        stream.Write("palladin.agent-encryption-key-rotation.v1"u8);
        stream.WriteByte(0);
        stream.Write([0, 2]);
        stream.Write(AgentId.ToByteArray(bigEndian: true));
        stream.Write(previous);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)suite.Length);
        stream.Write(length);
        stream.Write(suite);
        stream.Write(next);
        return stream.ToArray();
    }
}
