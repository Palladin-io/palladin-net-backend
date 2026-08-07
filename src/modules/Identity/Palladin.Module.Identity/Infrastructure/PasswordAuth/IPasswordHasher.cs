namespace Palladin.Module.Identity.Infrastructure.PasswordAuth;

internal interface IPasswordHasher
{
    // Re-hashes the client-supplied authHash with a fresh per-record server salt.
    (byte[] Hash, byte[] ServerSalt) Hash(byte[] clientAuthHash);

    // Constant-time verification against the stored server hash.
    bool Verify(byte[] clientAuthHash, byte[] expectedHash, byte[] serverSalt);
}
