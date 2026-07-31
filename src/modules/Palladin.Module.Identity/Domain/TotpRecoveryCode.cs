using NodaTime;

namespace Palladin.Module.Identity.Domain;

// One single-use TOTP recovery code. Only the hash is stored — the plaintext is shown to the user
// exactly once at enrollment.
internal sealed class TotpRecoveryCode
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public Instant? UsedAt { get; private set; }

    public TotpCredential Credential { get; private set; } = null!;

    private TotpRecoveryCode() { }

    internal static TotpRecoveryCode Create(Guid id, Guid userId, string codeHash) =>
        new()
        {
            Id = id,
            UserId = userId,
            CodeHash = codeHash,
        };

    internal bool IsAvailable => UsedAt is null;

    internal void MarkUsed(Instant now) => UsedAt = now;
}
