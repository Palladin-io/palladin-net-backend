using System.Buffers.Binary;
using System.Text;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class VaultKdfContextCodec
{
    private static readonly byte[] Magic = "PLDNKDF2"u8.ToArray();

    internal static byte[] Encode(
        CryptoSuiteId suiteId,
        EnvelopePurpose purpose,
        EnvelopeScope scope,
        uint keyVersion,
        uint? memberKeyGeneration)
    {
        ValidatePurposeScope(purpose, scope);
        if (keyVersion == 0 || memberKeyGeneration is 0)
            throw new DomainException("KDF key version and generation are invalid.");

        using var stream = new MemoryStream(160);
        stream.Write(Magic);
        WriteUInt16(stream, VaultProtocol.CurrentVersion);
        var suite = Encoding.ASCII.GetBytes(suiteId.Value);
        WriteUInt16(stream, checked((ushort)suite.Length));
        stream.Write(suite);
        WriteUInt16(stream, (ushort)purpose);
        WriteScope(stream, scope);
        WriteUInt32(stream, keyVersion);
        stream.WriteByte(memberKeyGeneration.HasValue ? (byte)1 : (byte)0);
        if (memberKeyGeneration.HasValue) WriteUInt32(stream, memberKeyGeneration.Value);
        return stream.ToArray();
    }

    private static void ValidatePurposeScope(EnvelopePurpose purpose, EnvelopeScope scope)
    {
        var vault = EnvelopeScopeFields.Organization | EnvelopeScopeFields.Vault;
        var entry = vault | EnvelopeScopeFields.Entry;
        var expected = purpose switch
        {
            EnvelopePurpose.MemberVaultMetadata or EnvelopePurpose.VaultDiscoveryKey
                or EnvelopePurpose.VaultAgentMessagePrivateKey
                or EnvelopePurpose.VaultManifestSigningPrivateKey => vault,
            EnvelopePurpose.MemberIndex or EnvelopePurpose.MemberSecret
                or EnvelopePurpose.AgentDiscovery or EnvelopePurpose.EntryDekByVaultKey => entry,
            EnvelopePurpose.EncryptedReason or EnvelopePurpose.GrantPayload =>
                entry | EnvelopeScopeFields.GrantOrRequest | EnvelopeScopeFields.Agent,
            _ => throw new DomainException("KDF purpose is not registered."),
        };
        scope.Validate(expected);
    }

    private static void WriteScope(Stream stream, EnvelopeScope scope)
    {
        WriteUInt16(stream, (ushort)scope.Fields);
        WriteGuid(stream, scope.OrganizationId);
        WriteGuid(stream, scope.VaultId);
        if (scope.EntryId.HasValue) WriteGuid(stream, scope.EntryId.Value);
        if (scope.GrantOrRequestId.HasValue) WriteGuid(stream, scope.GrantOrRequestId.Value);
        if (scope.AgentId.HasValue) WriteGuid(stream, scope.AgentId.Value);
        if (scope.MemberId.HasValue) WriteGuid(stream, scope.MemberId.Value);
    }

    private static void WriteGuid(Stream stream, Guid value)
    { Span<byte> b = stackalloc byte[16]; value.TryWriteBytes(b, true, out _); stream.Write(b); }
    private static void WriteUInt16(Stream stream, ushort value)
    { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); stream.Write(b); }
    private static void WriteUInt32(Stream stream, uint value)
    { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); stream.Write(b); }
}
