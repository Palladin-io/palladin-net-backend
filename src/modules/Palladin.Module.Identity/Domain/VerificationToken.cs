using Palladin.Core.Events;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Domain.Enums;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

// Only the token HASH is stored. The plaintext lives in the emailed link and, transiently, in the
// EmailVerificationRequestedEvent that carries it to the email trigger — never in logs or at rest.
internal sealed class VerificationToken : EventEntityBase
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public VerificationTokenPurpose Purpose { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public Instant ExpiresAt { get; private set; }
    public Instant? ConsumedAt { get; private set; }
    public Instant CreatedAt { get; private set; }

    public User User { get; private set; } = null!;

    private VerificationToken() { }

    internal static VerificationToken CreateEmailVerification(
        Guid id,
        Guid userId,
        string email,
        string language,
        string token,
        string tokenHash,
        Duration ttl,
        Instant now)
    {
        var entry = Create(id, userId, VerificationTokenPurpose.EmailVerify, tokenHash, ttl, now);
        entry.AddEvent(new EmailVerificationRequestedEvent(
            userId, email, language, token, (int)ttl.TotalMinutes, now));
        return entry;
    }

    internal static VerificationToken CreateLoginChallenge(
        Guid id,
        Guid userId,
        string tokenHash,
        Duration ttl,
        Instant now) =>
        Create(id, userId, VerificationTokenPurpose.LoginTotpChallenge, tokenHash, ttl, now);

    internal bool CanConsume(Instant now) => ConsumedAt is null && now <= ExpiresAt;

    internal void Consume(Instant now) => ConsumedAt = now;

    private static VerificationToken Create(
        Guid id,
        Guid userId,
        VerificationTokenPurpose purpose,
        string tokenHash,
        Duration ttl,
        Instant now) =>
        new()
        {
            Id = id,
            UserId = userId,
            Purpose = purpose,
            TokenHash = tokenHash,
            ExpiresAt = now + ttl,
            CreatedAt = now,
        };
}
