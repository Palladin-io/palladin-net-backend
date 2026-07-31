using Palladin.Core.Events;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

// TOTP second factor for a user (1:1). The secret is stored server-side and never logged. During
// enrollment it lives in PendingSecret; only a successful confirm promotes it to the active Secret
// and enables the factor. LastUsedTimeStep blocks replay of an already-accepted code within the
// verification window.
internal sealed class TotpCredential : EventEntityBase
{
    public Guid UserId { get; private set; }
    public string? PendingSecret { get; private set; }
    public string? Secret { get; private set; }
    public bool IsEnabled { get; private set; }
    public long LastUsedTimeStep { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }
    public Instant? ConfirmedAt { get; private set; }

    public User User { get; private set; } = null!;
    public ICollection<TotpRecoveryCode> RecoveryCodes { get; private set; } = [];

    private TotpCredential() { }

    internal static TotpCredential StartEnrollment(Guid userId, string secret, Instant now) =>
        new()
        {
            UserId = userId,
            PendingSecret = secret,
            IsEnabled = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal void RestartEnrollment(string secret, Instant now)
    {
        PendingSecret = secret;
        UpdatedAt = now;
    }

    // Recovery codes are persisted by the caller through the domain context so EF inserts them as new
    // rows rather than mistaking collection-fixup for updates. matchedTimeStep is the step the
    // confirmation code matched — recording it blocks reusing that same code on the first login/totp
    // (or disable) within the verification window.
    internal void Confirm(long matchedTimeStep, Instant now)
    {
        Secret = PendingSecret;
        PendingSecret = null;
        IsEnabled = true;
        ConfirmedAt = now;
        LastUsedTimeStep = matchedTimeStep;
        UpdatedAt = now;

        AddEvent(new TotpEnabledEvent(UserId, now));
    }

    internal void Disable(Instant now)
    {
        Secret = null;
        PendingSecret = null;
        IsEnabled = false;
        ConfirmedAt = null;
        RecoveryCodes.Clear();
        UpdatedAt = now;
    }

    internal void RecordUsedTimeStep(long timeStep, Instant now)
    {
        LastUsedTimeStep = timeStep;
        UpdatedAt = now;
    }

    internal TotpRecoveryCode? FindAvailableRecoveryCode(string codeHash) =>
        RecoveryCodes.FirstOrDefault(c => c.IsAvailable && c.CodeHash == codeHash);
}
