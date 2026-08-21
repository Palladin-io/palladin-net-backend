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
        Instant now) =>
        new()
        {
            Id = id,
            UserId = userId,
            OrganizationId = organizationId,
            TokenHash = tokenHash,
            AuthorizationVersion = authorizationVersion,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        };

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
