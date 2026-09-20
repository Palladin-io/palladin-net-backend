using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Module.Vault.Domain;

internal sealed class EntryShareSession : EventEntityBase
{
    public Guid ShareId { get; private set; }
    public Guid Id { get; private set; }
    public byte[] TokenHash { get; private set; } = [];
    public long SecurityVersion { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant ExpiresAt { get; private set; }
    public Instant? EmailVerifiedAt { get; private set; }
    public Instant? SecretVerifiedAt { get; private set; }
    public Instant? DeliveredAt { get; private set; }
    public Instant? ConfirmedAt { get; private set; }
    public byte[]? OtpHash { get; private set; }
    public Instant? OtpExpiresAt { get; private set; }
    public long OtpGeneration { get; private set; }
    public string? ProtectedOtp { get; private set; }
    public string? OtpLanguage { get; private set; }
    public long MutationVersion { get; private set; }

    private EntryShareSession() { }

    internal static EntryShareSession Create(
        Guid shareId, Guid id, byte[] tokenHash, long securityVersion, Instant now, Instant expiresAt)
    {
        if (shareId == Guid.Empty || id == Guid.Empty || tokenHash.Length != 32
            || securityVersion < 1 || expiresAt <= now)
        {
            throw new DomainException("A sharing session requires a bounded lifetime and a token verifier.");
        }

        return new EntryShareSession
        {
            ShareId = shareId,
            Id = id,
            TokenHash = tokenHash.ToArray(),
            SecurityVersion = securityVersion,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            MutationVersion = 1,
        };
    }

    internal void IssueOtp(byte[] hash, Instant expiresAt, EntryShareOtpDelivery delivery, Instant now)
    {
        if (hash.Length != 32 || expiresAt <= now || expiresAt > ExpiresAt
            || delivery.Generation != checked(OtpGeneration + 1)
            || string.IsNullOrWhiteSpace(delivery.ProtectedCode) || delivery.ProtectedCode.Length > 128
            || delivery.Language is not ("en" or "pl"))
        {
            throw new DomainException("The sharing code must expire within its session.");
        }

        OtpHash = hash.ToArray();
        OtpExpiresAt = expiresAt;
        OtpGeneration = delivery.Generation;
        ProtectedOtp = delivery.ProtectedCode;
        OtpLanguage = delivery.Language;
        EmailVerifiedAt = null;
        MutationVersion = checked(MutationVersion + 1);
        AddEvent(new EntryShareOtpRequestedEvent(ShareId, Id, OtpGeneration, now));
    }

    internal void FenceOtpDelivery() => MutationVersion = checked(MutationVersion + 1);

    internal void ClearPendingOtp()
    {
        ProtectedOtp = null;
        OtpLanguage = null;
        MutationVersion = checked(MutationVersion + 1);
    }

    internal void VerifyEmail(Instant now)
    {
        if (OtpHash is null || OtpExpiresAt is null || now >= OtpExpiresAt || now >= ExpiresAt)
        {
            throw new EntryShareUnavailableException();
        }

        EmailVerifiedAt = now;
        OtpHash = null;
        OtpExpiresAt = null;
        ClearPendingOtp();
        MutationVersion = checked(MutationVersion + 1);
    }

    internal void VerifySecret(Instant now)
    {
        if (now >= ExpiresAt)
        {
            throw new EntryShareUnavailableException();
        }

        SecretVerifiedAt ??= now;
        MutationVersion = checked(MutationVersion + 1);
    }

    internal void MarkDelivered(Instant now)
    {
        DeliveredAt = now;
        MutationVersion = checked(MutationVersion + 1);
    }

    internal bool Confirm(Instant now)
    {
        if (DeliveredAt is null)
        {
            throw new EntryShareUnavailableException();
        }

        if (ConfirmedAt is not null)
        {
            return false;
        }

        ConfirmedAt = now;
        MutationVersion = checked(MutationVersion + 1);
        return true;
    }
}
