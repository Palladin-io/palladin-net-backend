using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class EntryShareSession
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

    internal void IssueOtp(byte[] hash, Instant expiresAt)
    {
        if (hash.Length != 32 || expiresAt <= CreatedAt || expiresAt > ExpiresAt)
        {
            throw new DomainException("The sharing code must expire within its session.");
        }

        OtpHash = hash.ToArray();
        OtpExpiresAt = expiresAt;
        EmailVerifiedAt = null;
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
