using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

// A landing-page waitlist signup (double opt-in). Only the token HASH is stored — the plaintext
// token exists in the verification link and, transiently, in the WaitlistUpsertedEvent that carries
// it to the email trigger.
internal sealed class WaitlistEntry : EventEntityBase
{
    private const int DeveloperBenefitDurationMonths = 1;

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Language { get; private set; } = "en";
    public string TokenHash { get; private set; } = string.Empty;
    public Instant TokenIssuedAt { get; private set; }
    public Instant TokenExpiresAt { get; private set; }
    public Instant? VerifiedAt { get; private set; }
    public Guid? DeveloperBenefitUserId { get; private set; }
    public Instant? DeveloperBenefitStartedAt { get; private set; }
    public Instant? DeveloperBenefitEndsAt { get; private set; }
    public Instant CreatedAt { get; private set; }

    private WaitlistEntry() { }

    internal static WaitlistEntry Join(
        Guid id,
        string normalizedEmail,
        string language,
        string token,
        string tokenHash,
        Duration tokenTtl,
        Instant now)
    {
        var entry = new WaitlistEntry
        {
            Id = id,
            Email = normalizedEmail,
            Language = language,
            TokenHash = tokenHash,
            TokenIssuedAt = now,
            TokenExpiresAt = now + tokenTtl,
            CreatedAt = now,
        };
        entry.EmitUpserted(token, EntityChange.Created);
        return entry;
    }

    internal bool CanReissueToken(Duration resendCooldown, Instant now) =>
        VerifiedAt is null && now - TokenIssuedAt >= resendCooldown;

    internal void ReissueToken(string token, string tokenHash, Duration tokenTtl, Instant now)
    {
        TokenHash = tokenHash;
        TokenIssuedAt = now;
        TokenExpiresAt = now + tokenTtl;
        EmitUpserted(token, EntityChange.Updated);
    }

    internal bool CanVerify(Instant now) => VerifiedAt is null && now <= TokenExpiresAt;

    internal void Verify(Instant now)
    {
        if (!CanVerify(now))
        {
            return;
        }

        VerifiedAt = now;
        EmitVerified(now);
    }

    internal Instant? ClaimDeveloperBenefit(Guid userId, Instant accountCreatedAt, Instant now)
    {
        if (VerifiedAt is null
            || CreatedAt > accountCreatedAt
            || DeveloperBenefitUserId is not null)
        {
            return null;
        }

        var endsAt = now.InUtc().LocalDateTime
            .PlusMonths(DeveloperBenefitDurationMonths)
            .InUtc()
            .ToInstant();
        DeveloperBenefitUserId = userId;
        DeveloperBenefitStartedAt = now;
        DeveloperBenefitEndsAt = endsAt;
        return endsAt;
    }

    private void EmitUpserted(string token, EntityChange change) =>
        AddOrReplaceEvent(new WaitlistUpsertedEvent(
            Id, Email, Language, token, (int)(TokenExpiresAt - TokenIssuedAt).TotalHours, TokenIssuedAt, change));

    private void EmitVerified(Instant now) => AddEvent(new WaitlistVerifiedEvent(Id, now));
}
