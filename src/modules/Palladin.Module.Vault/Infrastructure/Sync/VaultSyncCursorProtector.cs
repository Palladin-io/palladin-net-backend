using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Sync;

internal enum VaultSyncPrincipalType : byte
{
    Member = 1,
    Agent = 2,
}

internal enum VaultSyncAudience : byte
{
    Member = 1,
    Discovery = 2,
}

internal readonly record struct VaultSyncCursorContext(
    VaultSyncPrincipalType PrincipalType,
    Guid PrincipalId,
    Guid OrganizationId,
    Guid VaultId,
    VaultSyncAudience Audience,
    uint KeyGeneration);

internal readonly record struct SnapshotCursorPayload(ulong SnapshotBaseSequence, Guid LastEntryId);

internal readonly record struct DeltaCursorPayload(
    ulong InitialAfterSequence,
    ulong LastSafeScannedSequence,
    ulong DeltaUpperBound);

internal sealed class VaultSyncCursorProtector
{
    private const byte FormatVersion = 2;
    private const byte SnapshotKind = 1;
    private const byte DeltaKind = 2;
    private const int TagBytes = 32;
    private const int SnapshotPayloadBytes = 96;
    private const int DeltaPayloadBytes = 96;
    private const string Purpose = "PLDN-VAULT-SYNC-CURSOR-v1";
    private readonly byte[] key;
    private readonly IClock clock;

    public VaultSyncCursorProtector(IServerKeyDeriver keyDeriver, IClock clock)
    {
        key = keyDeriver.DeriveHmacKey(Purpose);
        this.clock = clock;
    }

    internal string ProtectSnapshot(VaultSyncCursorContext context, SnapshotCursorPayload payload)
    {
        var bytes = new byte[SnapshotPayloadBytes];
        WriteCommon(bytes, SnapshotKind, context, syncPolicyVersion: 0);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(72, 8), payload.SnapshotBaseSequence);
        payload.LastEntryId.TryWriteBytes(bytes.AsSpan(80, 16), bigEndian: true, out _);
        return Sign(bytes);
    }

    internal bool TryUnprotectSnapshot(
        string token,
        VaultSyncCursorContext expectedContext,
        out SnapshotCursorPayload payload)
    {
        payload = default;
        if (!TryVerify(token, SnapshotPayloadBytes, out var bytes)
            || !ValidateCommon(bytes, SnapshotKind, expectedContext, syncPolicyVersion: 0))
        {
            return false;
        }

        payload = new SnapshotCursorPayload(
            BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(72, 8)),
            new Guid(bytes.AsSpan(80, 16), bigEndian: true));
        return true;
    }

    internal string ProtectDelta(VaultSyncCursorContext context, DeltaCursorPayload payload)
    {
        var bytes = new byte[DeltaPayloadBytes];
        WriteCommon(bytes, DeltaKind, context, VaultSyncProtocol.SyncPolicyVersion);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(72, 8), payload.InitialAfterSequence);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(80, 8), payload.LastSafeScannedSequence);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(88, 8), payload.DeltaUpperBound);
        return Sign(bytes);
    }

    internal bool TryUnprotectDelta(
        string token,
        VaultSyncCursorContext expectedContext,
        out DeltaCursorPayload payload)
    {
        payload = default;
        if (!TryVerify(token, DeltaPayloadBytes, out var bytes)
            || !ValidateCommon(bytes, DeltaKind, expectedContext, VaultSyncProtocol.SyncPolicyVersion))
        {
            return false;
        }

        payload = new DeltaCursorPayload(
            BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(72, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(80, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(88, 8)));
        return payload.InitialAfterSequence <= payload.LastSafeScannedSequence
               && payload.LastSafeScannedSequence <= payload.DeltaUpperBound;
    }

    private void WriteCommon(
        Span<byte> destination,
        byte kind,
        VaultSyncCursorContext context,
        ushort syncPolicyVersion)
    {
        destination[0] = (byte)'P';
        destination[1] = (byte)'S';
        destination[2] = (byte)'C';
        destination[3] = (byte)'V';
        destination[4] = FormatVersion;
        destination[5] = kind;
        destination[6] = (byte)context.PrincipalType;
        destination[7] = (byte)context.Audience;
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..10], VaultProtocol.CurrentVersion);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..12], syncPolicyVersion);
        BinaryPrimitives.WriteInt64BigEndian(
            destination[12..20],
            clock.GetCurrentInstant().Plus(Duration.FromSeconds(VaultSyncProtocol.CursorTtlSeconds)).ToUnixTimeSeconds());
        context.PrincipalId.TryWriteBytes(destination[20..36], bigEndian: true, out _);
        context.OrganizationId.TryWriteBytes(destination[36..52], bigEndian: true, out _);
        context.VaultId.TryWriteBytes(destination[52..68], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(destination[68..72], context.KeyGeneration);
    }

    private bool ValidateCommon(
        ReadOnlySpan<byte> bytes,
        byte kind,
        VaultSyncCursorContext expectedContext,
        ushort syncPolicyVersion) =>
        bytes[0] == (byte)'P'
        && bytes[1] == (byte)'S'
        && bytes[2] == (byte)'C'
        && bytes[3] == (byte)'V'
        && bytes[4] == FormatVersion
        && bytes[5] == kind
        && bytes[6] == (byte)expectedContext.PrincipalType
        && bytes[7] == (byte)expectedContext.Audience
        && BinaryPrimitives.ReadUInt16BigEndian(bytes[8..10]) == VaultProtocol.CurrentVersion
        && BinaryPrimitives.ReadUInt16BigEndian(bytes[10..12]) == syncPolicyVersion
        && BinaryPrimitives.ReadInt64BigEndian(bytes[12..20]) > clock.GetCurrentInstant().ToUnixTimeSeconds()
        && new Guid(bytes[20..36], bigEndian: true) == expectedContext.PrincipalId
        && new Guid(bytes[36..52], bigEndian: true) == expectedContext.OrganizationId
        && new Guid(bytes[52..68], bigEndian: true) == expectedContext.VaultId
        && BinaryPrimitives.ReadUInt32BigEndian(bytes[68..72]) == expectedContext.KeyGeneration;

    private string Sign(byte[] payload)
    {
        var signed = new byte[payload.Length + TagBytes];
        payload.CopyTo(signed, 0);
        HMACSHA256.HashData(key, payload).CopyTo(signed, payload.Length);
        return WebEncoders.Base64UrlEncode(signed);
    }

    private bool TryVerify(string token, int expectedPayloadBytes, out byte[] payload)
    {
        payload = [];
        byte[] signed;
        try
        {
            signed = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        if (signed.Length != expectedPayloadBytes + TagBytes)
        {
            return false;
        }

        var candidatePayload = signed.AsSpan(0, expectedPayloadBytes);
        var expectedTag = HMACSHA256.HashData(key, candidatePayload);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, signed.AsSpan(expectedPayloadBytes, TagBytes)))
        {
            return false;
        }

        payload = candidatePayload.ToArray();
        return true;
    }
}
