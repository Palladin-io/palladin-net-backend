using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class X25519WrapperContextCodec
{
    private static readonly byte[] Magic = "PLDNX2W1"u8.ToArray();
    private static readonly byte[] HashDomain = "PLDNX2CTX"u8.ToArray();

    internal static byte[] Encode(X25519WrapperContext context)
    {
        Validate(context);
        using var stream = new MemoryStream(192);
        stream.Write(Magic);
        WriteUInt16(stream, VaultProtocol.CurrentVersion);
        WriteAscii(stream, X25519SealedBoxContract.SuiteId);
        WriteUInt16(stream, (ushort)context.Purpose);
        WriteScope(stream, context.Scope);
        WriteUInt64(stream, context.ResourceRevision);
        WriteUInt32(stream, context.WrappedKeyVersion);
        stream.WriteByte(context.MemberKeyGeneration.HasValue ? (byte)1 : (byte)0);
        if (context.MemberKeyGeneration.HasValue) WriteUInt32(stream, context.MemberKeyGeneration.Value);
        WriteUInt16(stream, (ushort)context.RecipientKeyKind);
        WriteUInt32(stream, context.RecipientKeyVersion);
        stream.Write(context.RecipientFingerprint);
        stream.WriteByte(context.ParentDescriptorHash is null ? (byte)0 : (byte)1);
        if (context.ParentDescriptorHash is not null) stream.Write(context.ParentDescriptorHash);
        return stream.ToArray();
    }

    internal static byte[] ComputeContextHash(X25519WrapperContext context)
    {
        var encoded = Encode(context);
        var preimage = new byte[HashDomain.Length + encoded.Length];
        HashDomain.CopyTo(preimage, 0);
        encoded.CopyTo(preimage, HashDomain.Length);
        return SHA256.HashData(preimage);
    }

    internal static byte[] ComputeParentDescriptorHash(EnvelopeDescriptor descriptor) =>
        SHA256.HashData(EnvelopeDescriptorCodec.Encode(descriptor));

    private static void Validate(X25519WrapperContext context)
    {
        var vault = EnvelopeScopeFields.Organization | EnvelopeScopeFields.Vault;
        switch (context.Purpose)
        {
            case X25519WrapperPurpose.MemberVaultKey:
                context.Scope.Validate(vault | EnvelopeScopeFields.Member);
                if (context.RecipientKeyKind != VaultKeyKind.MemberX25519 || context.ParentDescriptorHash is not null)
                    throw new DomainException("Member Vault-key wrapper context is invalid.");
                break;
            case X25519WrapperPurpose.AgentDiscoveryVdk:
                context.Scope.Validate(vault | EnvelopeScopeFields.Agent);
                if (context.RecipientKeyKind != VaultKeyKind.AgentX25519 || context.ParentDescriptorHash is not null)
                    throw new DomainException("Agent Discovery-key wrapper context is invalid.");
                break;
            case X25519WrapperPurpose.ReasonDek:
                ValidateParentBound(context, vault | EnvelopeScopeFields.Entry
                    | EnvelopeScopeFields.GrantOrRequest | EnvelopeScopeFields.Agent,
                    VaultKeyKind.VaultMessageX25519);
                break;
            case X25519WrapperPurpose.GrantDek:
                ValidateParentBound(context, vault | EnvelopeScopeFields.Entry
                    | EnvelopeScopeFields.GrantOrRequest | EnvelopeScopeFields.Agent,
                    VaultKeyKind.AgentX25519);
                break;
            case X25519WrapperPurpose.AgentVaultKey:
                context.Scope.Validate(vault | EnvelopeScopeFields.GrantOrRequest | EnvelopeScopeFields.Agent);
                if (context.RecipientKeyKind != VaultKeyKind.AgentX25519
                    || context.MemberKeyGeneration is not null
                    || context.ParentDescriptorHash is not null)
                {
                    throw new DomainException("Agent Vault-key wrapper context is invalid.");
                }
                break;
            default:
                throw new DomainException("X25519 wrapper purpose is not registered.");
        }

        if (context.ResourceRevision == 0 || context.WrappedKeyVersion == 0
            || context.MemberKeyGeneration is 0 || context.RecipientKeyVersion == 0
            || context.RecipientFingerprint.Length != VaultProtocol.FingerprintBytes)
            throw new DomainException("X25519 wrapper version or recipient is invalid.");
    }

    private static void ValidateParentBound(
        X25519WrapperContext context,
        EnvelopeScopeFields scope,
        VaultKeyKind keyKind)
    {
        context.Scope.Validate(scope);
        if (context.RecipientKeyKind != keyKind || context.ParentDescriptorHash?.Length != 32)
            throw new DomainException("Parent-bound X25519 wrapper context is invalid.");
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
    private static void WriteAscii(Stream stream, string value)
    { var b = Encoding.ASCII.GetBytes(value); WriteUInt16(stream, checked((ushort)b.Length)); stream.Write(b); }
    private static void WriteUInt16(Stream stream, ushort value)
    { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); stream.Write(b); }
    private static void WriteUInt32(Stream stream, uint value)
    { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); stream.Write(b); }
    private static void WriteUInt64(Stream stream, ulong value)
    { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); stream.Write(b); }
}
