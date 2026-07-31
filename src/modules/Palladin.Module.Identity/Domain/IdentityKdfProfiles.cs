namespace Palladin.Module.Identity.Domain;

internal static class IdentityKdfProfiles
{
    internal const ushort CurrentSecurityVersion = 1;
    internal const string CurrentProfileId = "identity-argon2id-password-v1";
    internal const int KdfSaltBytes = 16;
    internal const int AuthCredentialBytes = 32;
    internal const int MemoryKiB = 32768;
    internal const int Iterations = 2;
    internal const int Parallelism = 1;
    internal const int OutputBytes = 32;
    internal const string AuthCredentialInfo = "palladin/identity/password-v1/auth-credential";
    internal const string MasterKeyInfo = "palladin/identity/password-v1/master-key";

    internal static bool IsCurrentAccountId(Guid accountId) =>
        accountId != Guid.Empty && accountId.ToString("N")[12] == '4';
}
