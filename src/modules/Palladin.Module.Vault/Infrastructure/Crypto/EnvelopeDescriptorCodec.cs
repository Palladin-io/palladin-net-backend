using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class EnvelopeDescriptorCodec
{
    private static readonly byte[] Magic = "PLDNENV2"u8.ToArray();
    private const string WrapperSuite = "palladin-x25519-sealed-box-v1";

    internal static byte[] Encode(EnvelopeDescriptor descriptor)
    {
        Validate(descriptor);
        using var stream = new MemoryStream(256);
        stream.Write(Magic);
        WriteUInt16(stream, descriptor.ProtocolVersion);
        WriteAscii(stream, descriptor.CryptoSuiteId.Value);
        WriteUInt16(stream, (ushort)descriptor.Purpose);
        WriteUInt16(stream, (ushort)descriptor.Scope.Fields);
        WriteGuid(stream, descriptor.Scope.OrganizationId);
        WriteGuid(stream, descriptor.Scope.VaultId);
        WriteOptionalGuid(stream, descriptor.Scope.EntryId);
        WriteOptionalGuid(stream, descriptor.Scope.GrantOrRequestId);
        WriteOptionalGuid(stream, descriptor.Scope.AgentId);
        WriteOptionalGuid(stream, descriptor.Scope.MemberId);
        WriteUInt64(stream, descriptor.ResourceRevision);
        WriteUInt32(stream, descriptor.KeyVersion);
        stream.WriteByte(descriptor.MemberKeyGeneration.HasValue ? (byte)1 : (byte)0);
        if (descriptor.MemberKeyGeneration.HasValue)
        {
            WriteUInt32(stream, descriptor.MemberKeyGeneration.Value);
        }

        WriteBinding(stream, descriptor.Binding);
        return stream.ToArray();
    }

    internal static byte[] ComputeFieldSetCommitment(IEnumerable<string> fieldIds)
    {
        var submitted = fieldIds.Select(ValidateFieldId).ToArray();
        if (submitted.Distinct(StringComparer.Ordinal).Count() != submitted.Length)
        {
            throw new DomainException("A grant field set cannot contain duplicate identifiers.");
        }

        var canonical = submitted.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (canonical.Length == 0)
        {
            throw new DomainException("A grant field set cannot be empty.");
        }

        using var stream = new MemoryStream();
        stream.Write("PLDNV2FS"u8);
        WriteUInt32(stream, checked((uint)canonical.Length));
        foreach (var fieldId in canonical)
        {
            WriteAscii(stream, fieldId);
        }

        return SHA256.HashData(stream.ToArray());
    }

    private static void Validate(EnvelopeDescriptor value)
    {
        if (value.ProtocolVersion != VaultProtocol.CurrentVersion
            || value.ResourceRevision == 0 || value.KeyVersion == 0
            || value.MemberKeyGeneration is 0)
        {
            throw new DomainException("Envelope descriptor versions are invalid.");
        }

        var entry = EnvelopeScopeFields.Organization | EnvelopeScopeFields.Vault | EnvelopeScopeFields.Entry;
        switch (value.Purpose)
        {
            case EnvelopePurpose.MemberVaultMetadata:
                value.Scope.Validate(EnvelopeScopeFields.Organization | EnvelopeScopeFields.Vault);
                RequireBinding<EmptyEnvelopeBinding>(value);
                break;
            case EnvelopePurpose.VaultDiscoveryKey:
            case EnvelopePurpose.VaultAgentMessagePrivateKey:
            case EnvelopePurpose.VaultManifestSigningPrivateKey:
                value.Scope.Validate(EnvelopeScopeFields.Organization | EnvelopeScopeFields.Vault);
                RequireBinding<VaultKeyEnvelopeBinding>(value);
                break;
            case EnvelopePurpose.MemberIndex:
            case EnvelopePurpose.AgentDiscovery:
                value.Scope.Validate(entry);
                RequireBinding<EmptyEnvelopeBinding>(value);
                break;
            case EnvelopePurpose.MemberSecret:
                value.Scope.Validate(entry);
                RequireBinding<MemberSecretEnvelopeBinding>(value);
                break;
            case EnvelopePurpose.EntryDekByVaultKey:
                value.Scope.Validate(entry);
                RequireBinding<VaultKeyEnvelopeBinding>(value);
                break;
            case EnvelopePurpose.EncryptedReason:
                value.Scope.Validate(entry | EnvelopeScopeFields.GrantOrRequest | EnvelopeScopeFields.Agent);
                RequireBinding<ReasonEnvelopeBinding>(value);
                break;
            case EnvelopePurpose.GrantPayload:
                value.Scope.Validate(entry | EnvelopeScopeFields.GrantOrRequest | EnvelopeScopeFields.Agent);
                RequireBinding<GrantEnvelopeBinding>(value);
                break;
            default:
                throw new DomainException("Envelope purpose is not registered.");
        }
    }

    private static void WriteBinding(Stream stream, EnvelopeBinding binding)
    {
        switch (binding)
        {
            case EmptyEnvelopeBinding:
                return;
            case MemberSecretEnvelopeBinding memberSecret:
                WriteUInt16(stream, memberSecret.Operation);
                return;
            case VaultKeyEnvelopeBinding vaultKey:
                WriteUInt32(stream, vaultKey.WrappingVaultKeyVersion);
                return;
            case ReasonEnvelopeBinding reason:
                WriteWrapperRecipient(stream, reason.WrapperSuiteId, reason.RecipientKeyVersion,
                    reason.RecipientKeyFingerprint);
                WriteUInt16(stream, reason.RequestedMethods);
                return;
            case GrantEnvelopeBinding grant:
                WriteUInt64(stream, grant.EntryRevision);
                WriteWrapperRecipient(stream, grant.WrapperSuiteId, grant.RecipientKeyVersion,
                    grant.RecipientKeyFingerprint);
                WriteUInt16(stream, grant.ApprovedMethods);
                WriteFixed(stream, grant.FieldSetCommitment, 32, "field-set commitment");
                WriteNullableInstant(stream, grant.ExpiresAtUnixSeconds, grant.ExpiresAtNanoseconds);
                stream.WriteByte(grant.RemainingUses.HasValue ? (byte)1 : (byte)0);
                if (grant.RemainingUses.HasValue) WriteUInt32(stream, grant.RemainingUses.Value);
                return;
            default:
                throw new DomainException("Envelope binding is not registered.");
        }
    }

    private static void WriteWrapperRecipient(Stream stream, string suiteId, uint version, byte[] fingerprint)
    {
        if (!string.Equals(suiteId, WrapperSuite, StringComparison.Ordinal) || version == 0)
            throw new DomainException("Recipient wrapper is not registered.");
        WriteAscii(stream, suiteId);
        WriteUInt32(stream, version);
        WriteFixed(stream, fingerprint, 32, "recipient fingerprint");
    }

    private static void WriteNullableInstant(Stream stream, long? seconds, uint? nanos)
    {
        if (seconds.HasValue != nanos.HasValue || nanos is >= 1_000_000_000)
            throw new DomainException("Grant expiry is not canonically encoded.");
        stream.WriteByte(seconds.HasValue ? (byte)1 : (byte)0);
        if (!seconds.HasValue) return;
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, seconds.Value);
        stream.Write(bytes);
        WriteUInt32(stream, nanos!.Value);
    }

    private static string ValidateFieldId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128
            || value.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                or '.' or '_' or ':' or '-')))
            throw new DomainException("Grant field identifiers must use canonical ASCII characters.");
        return value;
    }

    private static void RequireBinding<T>(EnvelopeDescriptor value) where T : EnvelopeBinding
    {
        if (value.Binding is not T) throw new DomainException("Envelope binding does not match its purpose.");
    }

    private static void WriteOptionalGuid(Stream stream, Guid? value)
    {
        if (value.HasValue) WriteGuid(stream, value.Value);
    }

    private static void WriteGuid(Stream stream, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        stream.Write(bytes);
    }

    private static void WriteAscii(Stream stream, string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => c > 0x7f))
            throw new DomainException("Protocol identifiers must use non-empty ASCII.");
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteUInt16(stream, checked((ushort)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteFixed(Stream stream, byte[] value, int length, string name)
    {
        if (value.Length != length) throw new DomainException($"The {name} must contain exactly {length} bytes.");
        stream.Write(value);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); stream.Write(b); }
    private static void WriteUInt32(Stream stream, uint value)
    { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); stream.Write(b); }
    private static void WriteUInt64(Stream stream, ulong value)
    { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); stream.Write(b); }
}
