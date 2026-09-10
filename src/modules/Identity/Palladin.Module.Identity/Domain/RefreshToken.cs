using Palladin.Core.Events;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class RefreshToken : EventEntityBase
{
    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid Id { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public uint AuthorizationVersion { get; private set; }
    public uint? SecondFactorRevision { get; private set; }
    public Instant? SecondFactorVerifiedAt { get; private set; }
    public Instant ExpiresAt { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant? RevokedAt { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }

    public User User { get; private set; } = null!;

    public bool IsExpired(Instant now) => now >= ExpiresAt;
    public bool IsRevoked => RevokedAt is not null;
    public bool IsActive(Instant now) => !IsRevoked && !IsExpired(now);

    public bool IsCloseToExpiry(Instant now, int thresholdDays) =>
        ExpiresAt.Minus(Duration.FromDays(thresholdDays)) <= now;

    private RefreshToken() { }

    internal static RefreshToken Create(
        Guid id,
        Guid userId,
        Guid organizationId,
        string tokenHash,
        uint authorizationVersion,
        Instant expiresAt,
        Instant now,
        uint? secondFactorRevision = null,
        Instant? secondFactorVerifiedAt = null)
    {
        if (secondFactorRevision.HasValue != secondFactorVerifiedAt.HasValue
            || secondFactorRevision is 0 || secondFactorVerifiedAt > now)
        {
            throw new ArgumentException("Invalid second-factor assurance.");
        }

        return new()
        {
            Id = id,
            UserId = userId,
            OrganizationId = organizationId,
            TokenHash = tokenHash,
            AuthorizationVersion = authorizationVersion,
            SecondFactorRevision = secondFactorRevision,
            SecondFactorVerifiedAt = secondFactorVerifiedAt,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        };
    }

    internal bool SatisfiesSecondFactor(TotpCredential? currentFactor, Instant now) =>
        IsActive(now)
        && (currentFactor is null
            || (currentFactor.UserId == UserId
                && (!currentFactor.IsEnabled
                    || (SecondFactorRevision == currentFactor.ConfigurationRevision
                        && SecondFactorVerifiedAt is { } verifiedAt && verifiedAt <= now))));

    internal void Revoke(Instant now, Guid? replacedByTokenId = null)
    {
        RevokedAt = now;
        ReplacedByTokenId = replacedByTokenId;
    }

    internal void RevokeForLogout(Instant now)
    {
        Revoke(now);
        AddEvent(new UserLoggedOutEvent(UserId));
    }
}
