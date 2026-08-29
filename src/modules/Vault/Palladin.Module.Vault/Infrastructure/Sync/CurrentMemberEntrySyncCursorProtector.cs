using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Sync;

internal readonly record struct CurrentMemberEntrySyncCursorContext(
    Guid PrincipalId,
    Guid OrganizationId,
    uint OrganizationMembershipGeneration,
    Guid VaultId,
    Guid MemberId,
    uint MemberKeyGeneration,
    uint VaultKeyVersion,
    uint OfflinePolicyVersion);

internal enum CurrentMemberEntrySyncCursorReadResult
{
    Valid,
    Invalid,
    StateChanged,
}

internal sealed class CurrentMemberEntrySyncCursorProtector
{
    private const byte FormatVersion = 1;
    private const byte SnapshotKind = 1;
    private const byte DeltaKind = 2;
    private const byte MemberPrincipalType = 1;
    private const byte CurrentEntryAudience = 3;
    private const int CommonBytes = 108;
    private const int PayloadBytes = 132;
    private const int TagBytes = 32;
    private const string Purpose = "PLDN-VAULT-CURRENT-ENTRY-SYNC-CURSOR-v2";

    private readonly byte[] key;
    private readonly IClock clock;

    public CurrentMemberEntrySyncCursorProtector(IServerKeyDeriver keyDeriver, IClock clock)
    {
        key = keyDeriver.DeriveHmacKey(Purpose);
        this.clock = clock;
    }

    internal string ProtectSnapshot(
        CurrentMemberEntrySyncCursorContext context,
        SnapshotCursorPayload payload)
    {
        var bytes = new byte[PayloadBytes];
        WriteCommon(bytes, SnapshotKind, context);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(CommonBytes, 8), payload.SnapshotBaseSequence);
        payload.LastEntryId.TryWriteBytes(bytes.AsSpan(CommonBytes + 8, 16), bigEndian: true, out _);
        return Sign(bytes);
    }

    internal CurrentMemberEntrySyncCursorReadResult TryUnprotectSnapshot(
        string token,
        CurrentMemberEntrySyncCursorContext expectedContext,
        out SnapshotCursorPayload payload)
    {
        payload = default;
        var result = TryReadCommon(token, SnapshotKind, expectedContext, out var bytes);
        if (result != CurrentMemberEntrySyncCursorReadResult.Valid)
        {
            return result;
        }

        payload = new SnapshotCursorPayload(
            BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(CommonBytes, 8)),
            new Guid(bytes.AsSpan(CommonBytes + 8, 16), bigEndian: true));
        return payload.LastEntryId == Guid.Empty
            ? CurrentMemberEntrySyncCursorReadResult.Invalid
            : CurrentMemberEntrySyncCursorReadResult.Valid;
    }

    internal string ProtectDelta(
        CurrentMemberEntrySyncCursorContext context,
        DeltaCursorPayload payload)
    {
        var bytes = new byte[PayloadBytes];
        WriteCommon(bytes, DeltaKind, context);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(CommonBytes, 8), payload.InitialAfterSequence);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(CommonBytes + 8, 8), payload.LastSafeScannedSequence);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(CommonBytes + 16, 8), payload.DeltaUpperBound);
        return Sign(bytes);
    }

    internal CurrentMemberEntrySyncCursorReadResult TryUnprotectDelta(
        string token,
        CurrentMemberEntrySyncCursorContext expectedContext,
        out DeltaCursorPayload payload)
    {
        payload = default;
        var result = TryReadCommon(token, DeltaKind, expectedContext, out var bytes);
        if (result != CurrentMemberEntrySyncCursorReadResult.Valid)
        {
            return result;
        }

        payload = new DeltaCursorPayload(
            BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(CommonBytes, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(CommonBytes + 8, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(CommonBytes + 16, 8)));
        return payload.InitialAfterSequence <= payload.LastSafeScannedSequence
               && payload.LastSafeScannedSequence <= payload.DeltaUpperBound
            ? CurrentMemberEntrySyncCursorReadResult.Valid
            : CurrentMemberEntrySyncCursorReadResult.Invalid;
    }

    private void WriteCommon(
        Span<byte> destination,
        byte kind,
        CurrentMemberEntrySyncCursorContext context)
    {
        destination[0] = (byte)'P';
        destination[1] = (byte)'C';
        destination[2] = (byte)'E';
        destination[3] = (byte)'S';
        destination[4] = FormatVersion;
        destination[5] = kind;
        destination[6] = MemberPrincipalType;
        destination[7] = CurrentEntryAudience;
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..10], VaultProtocol.CurrentVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[10..12], CurrentMemberEntrySyncProtocol.SyncPolicyVersion);
        BinaryPrimitives.WriteInt64BigEndian(
            destination[12..20],
            (clock.GetCurrentInstant() + Duration.FromSeconds(CurrentMemberEntrySyncProtocol.CursorTtlSeconds))
            .ToUnixTimeSeconds());
        context.PrincipalId.TryWriteBytes(destination[20..36], bigEndian: true, out _);
        context.OrganizationId.TryWriteBytes(destination[36..52], bigEndian: true, out _);
        context.VaultId.TryWriteBytes(destination[52..68], bigEndian: true, out _);
        context.MemberId.TryWriteBytes(destination[68..84], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(destination[84..92], context.OrganizationMembershipGeneration);
        BinaryPrimitives.WriteUInt32BigEndian(destination[92..96], context.MemberKeyGeneration);
        BinaryPrimitives.WriteUInt32BigEndian(destination[96..100], context.VaultKeyVersion);
        BinaryPrimitives.WriteUInt32BigEndian(destination[100..104], context.OfflinePolicyVersion);
        BinaryPrimitives.WriteUInt32BigEndian(destination[104..108], 0);
    }

    private CurrentMemberEntrySyncCursorReadResult TryReadCommon(
        string token,
        byte kind,
        CurrentMemberEntrySyncCursorContext expectedContext,
        out byte[] bytes)
    {
        if (!TryVerify(token, out bytes)
            || bytes[0] != (byte)'P'
            || bytes[1] != (byte)'C'
            || bytes[2] != (byte)'E'
            || bytes[3] != (byte)'S'
            || bytes[4] != FormatVersion
            || bytes[5] != kind
            || bytes[6] != MemberPrincipalType
            || bytes[7] != CurrentEntryAudience
            || BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(8, 2)) != VaultProtocol.CurrentVersion
            || BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10, 2))
                != CurrentMemberEntrySyncProtocol.SyncPolicyVersion
            || BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(12, 8))
                <= clock.GetCurrentInstant().ToUnixTimeSeconds()
            || new Guid(bytes.AsSpan(20, 16), bigEndian: true) != expectedContext.PrincipalId
            || new Guid(bytes.AsSpan(36, 16), bigEndian: true) != expectedContext.OrganizationId
            || new Guid(bytes.AsSpan(52, 16), bigEndian: true) != expectedContext.VaultId
            || new Guid(bytes.AsSpan(68, 16), bigEndian: true) != expectedContext.MemberId)
        {
            return CurrentMemberEntrySyncCursorReadResult.Invalid;
        }

        return BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(84, 8))
                   != expectedContext.OrganizationMembershipGeneration
               || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(92, 4))
                   != expectedContext.MemberKeyGeneration
               || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(96, 4))
                   != expectedContext.VaultKeyVersion
               || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(100, 4))
                   != expectedContext.OfflinePolicyVersion
            ? CurrentMemberEntrySyncCursorReadResult.StateChanged
            : CurrentMemberEntrySyncCursorReadResult.Valid;
    }

    private string Sign(byte[] payload)
    {
        var signed = new byte[payload.Length + TagBytes];
        payload.CopyTo(signed, 0);
        HMACSHA256.HashData(key, payload).CopyTo(signed, payload.Length);
        return WebEncoders.Base64UrlEncode(signed);
    }

    private bool TryVerify(string token, out byte[] payload)
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

        if (signed.Length != PayloadBytes + TagBytes)
        {
            return false;
        }

        var candidate = signed.AsSpan(0, PayloadBytes);
        var expectedTag = HMACSHA256.HashData(key, candidate);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, signed.AsSpan(PayloadBytes, TagBytes)))
        {
            return false;
        }

        payload = candidate.ToArray();
        return true;
    }
}
