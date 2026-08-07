using NodaTime;

namespace Palladin.Module.Identity.Domain;

// Server-side proof of the client's authHash. The client authHash is itself an Argon2id output; we
// re-hash it with a per-record server salt so a DB leak yields no usable login credential. AuthSalt
// is the CLIENT salt, returned pre-login so the client can reproduce its authHash. No plaintext
// password or master key ever reaches this entity.
internal sealed class PasswordCredential
{
    public Guid UserId { get; private set; }
    public byte[] AuthHash { get; private set; } = [];
    public byte[] AuthSalt { get; private set; } = [];
    public byte[] ServerHashSalt { get; private set; } = [];
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    public User User { get; private set; } = null!;

    private PasswordCredential() { }

    internal static PasswordCredential Create(
        Guid userId,
        byte[] authHash,
        byte[] authSalt,
        byte[] serverHashSalt,
        Instant now) =>
        new()
        {
            UserId = userId,
            AuthHash = authHash,
            AuthSalt = authSalt,
            ServerHashSalt = serverHashSalt,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal void Reset(byte[] authHash, byte[] authSalt, byte[] serverHashSalt, Instant now)
    {
        AuthHash = authHash;
        AuthSalt = authSalt;
        ServerHashSalt = serverHashSalt;
        UpdatedAt = now;
    }
}
