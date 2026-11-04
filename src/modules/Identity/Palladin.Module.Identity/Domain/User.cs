using Palladin.Core.Cryptography;
using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using NodaTime;
using System.Security.Cryptography;

namespace Palladin.Module.Identity.Domain;

internal sealed class User : EventEntityBase
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string? AvatarUrl { get; private set; }

    public PreferredLanguage PreferredLanguage { get; private set; } = PreferredLanguage.Default;
    public bool SharedUnlockEnabled { get; private set; } = true;
    public uint SharedUnlockRevision { get; private set; } = 1;
    public uint SharedUnlockSequence { get; private set; }

    public byte[]? Salt { get; private set; }
    public byte[]? RecoverySalt { get; private set; }
    public byte[]? PublicKey { get; private set; }
    public string? MemberPublicKeySuiteId { get; private set; }
    public uint? MemberKeyVersion { get; private set; }
    public byte[]? LegacyEncryptedPrivateKey { get; private set; }
    public uint? LegacyMemberKeyVersion { get; private set; }
    public Guid? LastSecurityUpgradeId { get; private set; }
    public byte[]? EncryptedPrivateKey { get; private set; }
    public byte[]? EncryptedPrivateKeyByRecovery { get; private set; }
    public uint PrivateKeyWrapRevision { get; private set; }
    public ushort SecurityVersion { get; private set; }
    public ushort MinimumSecurityVersion { get; private set; }
    public string? KdfProfileId { get; private set; }
    public uint CredentialRevision { get; private set; }
    public byte[]? DeviceWrapperMetadata { get; private set; }
    public byte[]? RecoveryChallengeHash { get; private set; }
    public Instant? RecoveryChallengeExpiresAt { get; private set; }

    public bool EmailVerified { get; private set; }
    public Instant? EmailVerifiedAt { get; private set; }
    public Instant? WaitlistDeveloperBenefitStartedAt { get; private set; }
    public Instant? WaitlistDeveloperBenefitEndsAt { get; private set; }

    public Guid OrganizationId { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    public Organization Organization { get; private set; } = null!;
    public ICollection<OrganizationMember> OrganizationMemberships { get; private set; } = [];
    public ICollection<OAuthConnection> OAuthConnections { get; private set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; private set; } = [];
    public PasswordCredential? PasswordCredential { get; private set; }
    public TotpCredential? TotpCredential { get; private set; }

    public bool IsOnboarded { get; private set; }

    // Per-user onboarding milestones. Org-level steps (ApiKeyCreated / AgentEnrolled) live on Organization.
    public bool EntryCreated { get; private set; }
    public bool MobileRegistered { get; private set; }

    private User() { }

    internal bool TryAdvanceSharedUnlockSequence()
    {
        if (SharedUnlockSequence == uint.MaxValue)
        {
            return false;
        }

        SharedUnlockSequence++;
        return true;
    }

    internal bool TrySetSharedUnlockPreference(bool enabled, uint expectedRevision, Instant now)
    {
        if (SharedUnlockRevision != expectedRevision || SharedUnlockRevision == uint.MaxValue)
        {
            return false;
        }

        SharedUnlockEnabled = enabled;
        SharedUnlockRevision++;
        UpdatedAt = now;
        return true;
    }

    public Permission EffectivePermissions(Guid organizationId) =>
        OrganizationMemberships
            .Where(m => m.OrganizationId == organizationId)
            .Aggregate(Permission.None, (current, membership) =>
                current | membership.EffectivePermissions());

    internal static User Create(
        Guid id,
        string email,
        string displayName,
        string? avatarUrl,
        Guid organizationId,
        Instant now,
        string provider,
        string platform,
        Permission effectivePermissions)
    {
        var user = new User
        {
            Id = id,
            Email = email,
            DisplayName = displayName,
            AvatarUrl = avatarUrl,
            OrganizationId = organizationId,
            PreferredLanguage = PreferredLanguage.Default,
            // OAuth providers assert the email is verified (rejected upstream otherwise).
            EmailVerified = true,
            EmailVerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        user.EmitSignedUp(provider, platform);
        user.EmitUpserted(effectivePermissions);
        user.EmitLoggedIn(provider, platform, true);

        return user;
    }

    internal const string PasswordProvider = "password";

    internal static User RegisterWithPassword(
        Guid id,
        string email,
        string displayName,
        string? language,
        Guid organizationId,
        Permission effectivePermissions,
        byte[] salt,
        byte[] recoverySalt,
        string publicKeySuiteId,
        byte[] publicKey,
        byte[] encryptedPrivateKey,
        byte[] encryptedPrivateKeyByRecovery,
        byte[]? deviceWrapperMetadata,
        string platform,
        Instant now)
    {
        EnsureCurrentMemberKey(publicKeySuiteId, publicKey);
        var user = new User
        {
            Id = id,
            Email = email,
            DisplayName = displayName,
            OrganizationId = organizationId,
            PreferredLanguage = PreferredLanguage.From(language),
            Salt = salt,
            RecoverySalt = recoverySalt,
            PublicKey = publicKey,
            MemberPublicKeySuiteId = publicKeySuiteId,
            MemberKeyVersion = 1,
            EncryptedPrivateKey = encryptedPrivateKey,
            EncryptedPrivateKeyByRecovery = encryptedPrivateKeyByRecovery,
            DeviceWrapperMetadata = deviceWrapperMetadata,
            PrivateKeyWrapRevision = 1,
            SecurityVersion = IdentityKdfProfiles.Current.SecurityVersion,
            MinimumSecurityVersion = IdentityKdfProfiles.Current.SecurityVersion,
            KdfProfileId = IdentityKdfProfiles.Current.Id,
            CredentialRevision = 1,
            IsOnboarded = true,
            EmailVerified = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        user.EmitSignedUp(PasswordProvider, platform);
        user.EmitUpserted(effectivePermissions);
        user.EmitMemberKeyDirectoryUpserted();
        user.EmitLoggedIn(PasswordProvider, platform, true);
        user.EmitAccountSetupCompleted();

        return user;
    }

    internal void MarkEmailVerified(Instant now)
    {
        if (EmailVerified)
        {
            return;
        }

        EmailVerified = true;
        EmailVerifiedAt = now;
        UpdatedAt = now;

        AddEvent(new EmailVerifiedEvent(Id, OrganizationId, now));
    }

    internal void ActivateWaitlistDeveloperBenefit(Instant startsAt, Instant endsAt)
    {
        if (!EmailVerified
            || WaitlistDeveloperBenefitStartedAt is not null
            || endsAt <= startsAt)
        {
            return;
        }

        WaitlistDeveloperBenefitStartedAt = startsAt;
        WaitlistDeveloperBenefitEndsAt = endsAt;
        UpdatedAt = startsAt;
        AddEvent(new WaitlistDeveloperBenefitActivatedEvent(
            Id,
            OrganizationId,
            Email,
            PreferredLanguage.Code,
            startsAt,
            endsAt,
            startsAt));
    }

    internal Instant? ActiveWaitlistDeveloperBenefitEndsAt(Instant now) =>
        WaitlistDeveloperBenefitEndsAt is { } endsAt && now < endsAt
            ? endsAt
            : null;

    internal Instant? ActiveWaitlistDeveloperBenefitStartedAt(Instant now) =>
        ActiveWaitlistDeveloperBenefitEndsAt(now) is not null
            ? WaitlistDeveloperBenefitStartedAt
            : null;

    internal PlanType EffectivePlan(PlanType organizationPlan, Instant now) =>
        ActiveWaitlistDeveloperBenefitEndsAt(now) is not null
            && organizationPlan < PlanType.Pro
                ? PlanType.Pro
                : organizationPlan;

    internal void RecordLogin(string provider, string platform)
    {
        EmitLoggedIn(provider, platform, false);

        EmitUpserted(EffectivePermissions(OrganizationId));
    }

    internal void SetPreferredLanguage(string? language, Instant now)
    {
        PreferredLanguage = PreferredLanguage.From(language);
        UpdatedAt = now;
    }

    internal void SetupAccount(
        ushort securityVersion,
        string kdfProfileId,
        byte[] kdfSalt,
        byte[] recoverySalt,
        string publicKeySuiteId,
        byte[] publicKey,
        byte[] encryptedPrivateKey,
        byte[] encryptedPrivateKeyByRecovery,
        byte[]? deviceWrapperMetadata,
        Instant now)
    {
        if (!IdentityKdfProfiles.IsCurrent(securityVersion, kdfProfileId))
        {
            throw new InvalidOperationException("The current registered Identity KDF profile is required.");
        }

        EnsureCurrentMemberKey(publicKeySuiteId, publicKey);
        Salt = kdfSalt;
        RecoverySalt = recoverySalt;
        PublicKey = publicKey;
        MemberPublicKeySuiteId = publicKeySuiteId;
        MemberKeyVersion = 1;
        EncryptedPrivateKey = encryptedPrivateKey;
        EncryptedPrivateKeyByRecovery = encryptedPrivateKeyByRecovery;
        PrivateKeyWrapRevision = 1;
        SecurityVersion = IdentityKdfProfiles.Current.SecurityVersion;
        MinimumSecurityVersion = IdentityKdfProfiles.Current.SecurityVersion;
        KdfProfileId = IdentityKdfProfiles.Current.Id;
        CredentialRevision = 1;
        DeviceWrapperMetadata = deviceWrapperMetadata;
        IsOnboarded = true;
        UpdatedAt = now;

        EmitMemberKeyDirectoryUpserted();
        EmitAccountSetupCompleted();
    }

    internal void EnablePasswordCredential(Instant now)
    {
        CredentialRevision = 1;
        UpdatedAt = now;
    }

    internal bool HasCompletedSecurityUpgrade(Guid migrationId) =>
        LastSecurityUpgradeId == migrationId && IdentityKdfProfiles.IsCurrent(SecurityVersion, KdfProfileId);

    // Atomic in-place v1 -> v2 migration (identity-kdf v3 "migration"): every field changes in one domain commit and
    // the compare-and-swap bases reject a concurrent password change, recovery or second upgrade.
    internal void UpgradeSecurity(
        Guid migrationId,
        ushort sourceSecurityVersion,
        uint baseCredentialRevision,
        uint basePrivateKeyWrapRevision,
        uint baseMemberKeyVersion,
        byte[] kdfSalt,
        string memberPublicKeySuiteId,
        byte[] memberPublicKey,
        byte[] encryptedPrivateKey,
        byte[] recoverySalt,
        byte[] encryptedPrivateKeyByRecovery,
        byte[] legacyEncryptedPrivateKey,
        byte[]? deviceWrapperMetadata,
        Instant now)
    {
        if (migrationId == Guid.Empty
            || sourceSecurityVersion != SecurityVersion
            || IdentityKdfProfiles.Find(KdfProfileId) is not { } source
            || source.SecurityVersion != SecurityVersion
            || source.SecurityVersion >= IdentityKdfProfiles.Current.SecurityVersion
            || MinimumSecurityVersion > IdentityKdfProfiles.Current.SecurityVersion
            || MemberKeyVersion != baseMemberKeyVersion
            || MemberKeyVersion is null or uint.MaxValue
            || PublicKey is null)
        {
            throw new InvalidOperationException("Identity security upgrade compare-and-swap failed.");
        }

        EnsureRevisionMatch(baseCredentialRevision, basePrivateKeyWrapRevision);
        EnsureCurrentMemberKey(memberPublicKeySuiteId, memberPublicKey);
        if (PublicKey.AsSpan().SequenceEqual(memberPublicKey))
        {
            throw new InvalidOperationException("The security upgrade requires a new Member key pair.");
        }

        LegacyEncryptedPrivateKey = legacyEncryptedPrivateKey;
        LegacyMemberKeyVersion = MemberKeyVersion;
        Salt = kdfSalt;
        PublicKey = memberPublicKey;
        MemberPublicKeySuiteId = memberPublicKeySuiteId;
        MemberKeyVersion += 1;
        EncryptedPrivateKey = encryptedPrivateKey;
        RecoverySalt = recoverySalt;
        EncryptedPrivateKeyByRecovery = encryptedPrivateKeyByRecovery;
        DeviceWrapperMetadata = deviceWrapperMetadata;
        SecurityVersion = IdentityKdfProfiles.Current.SecurityVersion;
        MinimumSecurityVersion = IdentityKdfProfiles.Current.SecurityVersion;
        KdfProfileId = IdentityKdfProfiles.Current.Id;
        CredentialRevision = IncrementRevision(CredentialRevision);
        PrivateKeyWrapRevision = IncrementRevision(PrivateKeyWrapRevision);
        RecoveryChallengeHash = null;
        RecoveryChallengeExpiresAt = null;
        LastSecurityUpgradeId = migrationId;
        UpdatedAt = now;

        EmitMemberKeyDirectoryUpserted();
    }

    internal bool DeleteLegacyMemberKey(uint expectedLegacyMemberKeyVersion, Instant now)
    {
        if (LegacyMemberKeyVersion != expectedLegacyMemberKeyVersion || LegacyEncryptedPrivateKey is null)
        {
            return false;
        }

        LegacyEncryptedPrivateKey = null;
        LegacyMemberKeyVersion = null;
        UpdatedAt = now;
        return true;
    }

    internal void RepublishMemberKeyDirectory() => EmitMemberKeyDirectoryUpserted();

    internal bool MarkOnboardingStep(OnboardingStep step)
    {
        switch (step)
        {
            case OnboardingStep.EntryCreated when !EntryCreated:
                EntryCreated = true;
                return true;
            case OnboardingStep.MobileRegistered when !MobileRegistered:
                MobileRegistered = true;
                return true;
            default:
                return false;
        }
    }

    internal void DiscardUnverifiedAccount(Instant now)
    {
        if (EmailVerified)
        {
            throw new DomainException("A verified account can never be discarded for an OAuth sign-in.");
        }

        AddEvent(new UnverifiedAccountDiscardedEvent(Id, OrganizationId, now));
    }

    internal void IssueRecoveryChallenge(ReadOnlySpan<byte> secret, Instant expiresAt)
    {
        RecoveryChallengeHash = SHA256.HashData(secret);
        RecoveryChallengeExpiresAt = expiresAt;
    }

    // Single use: every presented proof burns the outstanding challenge, whether it matches or not.
    internal bool ConsumeRecoveryChallenge(byte[]? proof, Instant now)
    {
        var isValid = proof is not null
            && RecoveryChallengeHash is { } expectedHash
            && RecoveryChallengeExpiresAt > now
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(proof), expectedHash);
        RecoveryChallengeHash = null;
        RecoveryChallengeExpiresAt = null;
        return isValid;
    }

    internal void RecoverAccount(
        uint baseCredentialRevision,
        uint basePrivateKeyWrapRevision,
        byte[] newKdfSalt,
        byte[] newEncryptedPrivateKey,
        byte[] newRecoverySalt,
        byte[] newEncryptedPrivateKeyByRecovery,
        byte[]? newDeviceWrapperMetadata,
        bool rotatesPasswordCredential,
        string client,
        Instant now)
    {
        EnsureRegisteredKdfProfile();
        EnsureRevisionMatch(baseCredentialRevision, basePrivateKeyWrapRevision);

        foreach (var token in RefreshTokens.Where(t => t.IsActive(now)))
        {
            token.Revoke(now);
        }

        Salt = newKdfSalt;
        EncryptedPrivateKey = newEncryptedPrivateKey;
        RecoverySalt = newRecoverySalt;
        EncryptedPrivateKeyByRecovery = newEncryptedPrivateKeyByRecovery;
        CredentialRevision = rotatesPasswordCredential
            ? IncrementRevision(CredentialRevision)
            : CredentialRevision;
        PrivateKeyWrapRevision = IncrementRevision(PrivateKeyWrapRevision);
        DeviceWrapperMetadata = newDeviceWrapperMetadata;
        LegacyEncryptedPrivateKey = null;
        LegacyMemberKeyVersion = null;
        UpdatedAt = now;

        AddEvent(new AccountRecoveryCompletedEvent(
            Id, OrganizationId, DisplayName, Email, PreferredLanguage.Code, client, UpdatedAt));
    }

    // Master-password change (Variant A): re-wraps the private key under the new MK and rotates the enc
    // salt. The recovery mnemonic is an independent path and is deliberately left untouched. ALL active
    // refresh tokens are revoked (including the current session's — the request carries only a JWT, so
    // the current session cannot be singled out) forcing every session to re-authenticate.
    internal void ChangeMasterPassword(
        uint baseCredentialRevision,
        uint basePrivateKeyWrapRevision,
        byte[] newSalt,
        byte[] newEncryptedPrivateKey,
        Instant now)
    {
        EnsureRegisteredKdfProfile();
        EnsureRevisionMatch(baseCredentialRevision, basePrivateKeyWrapRevision);

        foreach (var token in RefreshTokens.Where(t => t.IsActive(now)))
        {
            token.Revoke(now);
        }

        Salt = newSalt;
        EncryptedPrivateKey = newEncryptedPrivateKey;
        CredentialRevision = IncrementRevision(CredentialRevision);
        PrivateKeyWrapRevision = IncrementRevision(PrivateKeyWrapRevision);
        UpdatedAt = now;
    }

    private void EnsureRegisteredKdfProfile()
    {
        if (!IdentityKdfProfiles.IsRegistered(SecurityVersion, KdfProfileId) || MinimumSecurityVersion > SecurityVersion)
        {
            throw new InvalidOperationException("The account's registered Identity KDF profile is required.");
        }
    }

    private static void EnsureCurrentMemberKey(string publicKeySuiteId, byte[] publicKey)
    {
        if (!string.Equals(publicKeySuiteId, KeyWrapperSuites.Current.Id, StringComparison.Ordinal)
            || publicKey.Length != KeyWrapperSuites.Current.PublicKeyBytes)
        {
            throw new InvalidOperationException("New Member keys must use the current key-wrapper suite.");
        }
    }

    private void EnsureRevisionMatch(uint baseCredentialRevision, uint basePrivateKeyWrapRevision)
    {
        if (baseCredentialRevision != CredentialRevision
            || basePrivateKeyWrapRevision != PrivateKeyWrapRevision)
        {
            throw new InvalidOperationException("Identity credential or private-key wrapper compare-and-swap failed.");
        }
    }

    private static uint IncrementRevision(uint revision)
    {
        if (revision == uint.MaxValue)
        {
            throw new InvalidOperationException("Identity revision is exhausted.");
        }

        return revision + 1;
    }

    private void EmitSignedUp(string provider, string platform) =>
        AddEvent(new UserSignedUpEvent(Id, OrganizationId, DisplayName, provider, platform, CreatedAt));

    private void EmitUpserted(Permission effectivePermissions) =>
        AddEvent(new UserUpsertedEvent(
            Id, OrganizationId, DisplayName, Email, effectivePermissions, UpdatedAt));

    private void EmitLoggedIn(string provider, string platform, bool isNewUser) =>
        AddEvent(new UserLoggedInEvent(Id, provider, platform, isNewUser));

    private void EmitAccountSetupCompleted() =>
        AddEvent(new AccountSetupCompletedEvent(Id, OrganizationId, DisplayName, UpdatedAt));

    private void EmitMemberKeyDirectoryUpserted()
    {
        if (PublicKey is null || MemberPublicKeySuiteId is null
            || PublicKey.Length != KeyWrapperSuites.ResolveForOpen(MemberPublicKeySuiteId).PublicKeyBytes)
        {
            throw new InvalidOperationException("The Member public key must match its registered key-wrapper suite.");
        }

        if (MemberKeyVersion is null or 0)
        {
            throw new InvalidOperationException("The Member public key version must be established before publication.");
        }

        AddEvent(new UpsertMemberKeyDirectoryCommand(
            Id, MemberKeyVersion.Value, MemberPublicKeySuiteId, PublicKey.ToArray(), UpdatedAt));
    }

}
