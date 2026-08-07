using Palladin.Core.Events;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

// A landing-page waitlist signup (double opt-in). Verified entries earn one free month of Premium
// at launch. Only the token HASH is stored — the plaintext token exists in the verification link
// and, transiently, in the WaitlistJoinedEvent that carries it to the email trigger.
internal sealed class WaitlistEntry : EventEntityBase
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Language { get; private set; } = "en";
    public string TokenHash { get; private set; } = string.Empty;
    public Instant TokenIssuedAt { get; private set; }
    public Instant TokenExpiresAt { get; private set; }
    public Instant? VerifiedAt { get; private set; }
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
        entry.EmitJoined(token);
        return entry;
    }

    internal bool CanReissueToken(Duration resendCooldown, Instant now) =>
        VerifiedAt is null && now - TokenIssuedAt >= resendCooldown;

    internal void ReissueToken(string token, string tokenHash, Duration tokenTtl, Instant now)
    {
        TokenHash = tokenHash;
        TokenIssuedAt = now;
        TokenExpiresAt = now + tokenTtl;
        EmitJoined(token);
    }

    internal bool CanVerify(Instant now) => VerifiedAt is null && now <= TokenExpiresAt;

    internal void Verify(Instant now)
    {
        VerifiedAt = now;
        EmitVerified(now);
    }

    private void EmitJoined(string token) =>
        AddOrReplaceEvent(new WaitlistJoinedEvent(
            Id, Email, Language, token, (int)(TokenExpiresAt - TokenIssuedAt).TotalHours, TokenIssuedAt));

    private void EmitVerified(Instant now) => AddEvent(new WaitlistVerifiedEvent(Id, now));
}
