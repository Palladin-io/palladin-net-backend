namespace Palladin.Module.Identity.Domain;

internal sealed record IdentityKdfProfile(
    string Id,
    ushort SecurityVersion,
    int MemoryKiB,
    int Iterations,
    int Parallelism,
    bool AccountSecretRequired);

// Registry-pinned profiles from palladin-protocol contracts/identity-kdf v2/v3. Parameters are never server-tunable.
internal static class IdentityKdfProfiles
{
    internal const int KdfSaltBytes = 16;
    internal const int AuthCredentialBytes = 32;
    internal const int OutputBytes = 32;
    internal const string AuthCredentialInfo = "palladin/identity/password-v1/auth-credential";
    internal const string MasterKeyInfo = "palladin/identity/password-v1/master-key";

    internal static IdentityKdfProfile PasswordV1 { get; } =
        new("identity-argon2id-password-v1", 1, 32768, 2, 1, false);

    internal static IdentityKdfProfile AccountSecretV2 { get; } =
        new("identity-argon2id-account-secret-v2", 2, 32768, 2, 1, true);

    internal static IdentityKdfProfile Current => AccountSecretV2;

    internal static IReadOnlyList<IdentityKdfProfile> All { get; } = [PasswordV1, AccountSecretV2];

    internal static IdentityKdfProfile? Find(string? profileId) =>
        All.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal));

    internal static bool IsRegistered(ushort securityVersion, string? profileId) =>
        Find(profileId) is { } profile && profile.SecurityVersion == securityVersion;

    internal static bool IsCurrent(ushort securityVersion, string? profileId) =>
        securityVersion == Current.SecurityVersion && string.Equals(profileId, Current.Id, StringComparison.Ordinal);

    internal static bool IsCurrentAccountId(Guid accountId) =>
        accountId != Guid.Empty && accountId.ToString("N")[12] == '4';
}
