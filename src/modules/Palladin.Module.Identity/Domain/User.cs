using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class User : EventEntityBase
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string? AvatarUrl { get; private set; }

    public PreferredLanguage PreferredLanguage { get; private set; } = PreferredLanguage.Default;

    public byte[]? Salt { get; private set; }
    public byte[]? RecoverySalt { get; private set; }
    public byte[]? PublicKey { get; private set; }
    public uint? MemberKeyVersion { get; private set; }
    public byte[]? EncryptedPrivateKey { get; private set; }
    public byte[]? EncryptedPrivateKeyByRecovery { get; private set; }
    public uint PrivateKeyWrapRevision { get; private set; }
    public ushort SecurityVersion { get; private set; }
    public ushort MinimumSecurityVersion { get; private set; }
    public string? KdfProfileId { get; private set; }
    public uint CredentialRevision { get; private set; }
    public byte[]? DeviceWrapperMetadata { get; private set; }

    public bool EmailVerified { get; private set; }
    public Instant? EmailVerifiedAt { get; private set; }

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
        byte[] publicKey,
        byte[] encryptedPrivateKey,
        byte[] encryptedPrivateKeyByRecovery,
        byte[]? deviceWrapperMetadata,
        string platform,
        Instant now)
    {
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
            MemberKeyVersion = 1,
            EncryptedPrivateKey = encryptedPrivateKey,
            EncryptedPrivateKeyByRecovery = encryptedPrivateKeyByRecovery,
            DeviceWrapperMetadata = deviceWrapperMetadata,
            PrivateKeyWrapRevision = 1,
            SecurityVersion = IdentityKdfProfiles.CurrentSecurityVersion,
            MinimumSecurityVersion = IdentityKdfProfiles.CurrentSecurityVersion,
            KdfProfileId = IdentityKdfProfiles.CurrentProfileId,
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
        byte[] publicKey,
        byte[] encryptedPrivateKey,
        byte[] encryptedPrivateKeyByRecovery,
        byte[]? deviceWrapperMetadata,
        Instant now)
    {
        EnsureCurrentKdfProfile(securityVersion, kdfProfileId);

        Salt = kdfSalt;
        RecoverySalt = recoverySalt;
        PublicKey = publicKey;
        MemberKeyVersion = 1;
        EncryptedPrivateKey = encryptedPrivateKey;
        EncryptedPrivateKeyByRecovery = encryptedPrivateKeyByRecovery;
        PrivateKeyWrapRevision = 1;
        SecurityVersion = IdentityKdfProfiles.CurrentSecurityVersion;
        MinimumSecurityVersion = IdentityKdfProfiles.CurrentSecurityVersion;
        KdfProfileId = IdentityKdfProfiles.CurrentProfileId;
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

    internal void RotateMemberKeyPair(
        byte[] publicKey,
        byte[] encryptedPrivateKey,
        byte[] encryptedPrivateKeyByRecovery,
        Instant now)
    {
        if (publicKey is not { Length: 32 })
        {
            throw new InvalidOperationException("The Member X25519 public key must contain exactly 32 raw bytes.");
        }

        if (PublicKey is null || MemberKeyVersion is null)
        {
            throw new InvalidOperationException("The initial Member public key must be established before rotation.");
        }

        if (PublicKey.AsSpan().SequenceEqual(publicKey))
        {
            throw new InvalidOperationException("Member public key rotation requires a new key.");
        }

        if (encryptedPrivateKey is not { Length: >= 32 and <= 4096 }
            || encryptedPrivateKeyByRecovery is not { Length: >= 32 and <= 4096 })
        {
            throw new InvalidOperationException("Member private-key ciphertexts must contain between 32 and 4096 bytes.");
        }

        if (MemberKeyVersion == uint.MaxValue)
        {
            throw new InvalidOperationException("The Member public key version is exhausted.");
        }

        PublicKey = publicKey;
        EncryptedPrivateKey = encryptedPrivateKey;
        EncryptedPrivateKeyByRecovery = encryptedPrivateKeyByRecovery;
        MemberKeyVersion += 1;
        PrivateKeyWrapRevision = IncrementRevision(PrivateKeyWrapRevision);
        UpdatedAt = now;

        EmitMemberKeyDirectoryUpserted();
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

    internal void RecoverAccount(
        uint baseCredentialRevision,
        uint basePrivateKeyWrapRevision,
        byte[] newKdfSalt,
        byte[] newEncryptedPrivateKey,
        byte[] newRecoverySalt,
        byte[] newEncryptedPrivateKeyByRecovery,
        byte[]? newDeviceWrapperMetadata,
        bool rotatesPasswordCredential,
        Instant now)
    {
        EnsureCurrentKdfProfile(SecurityVersion, KdfProfileId);
        EnsureRevisionMatch(baseCredentialRevision, basePrivateKeyWrapRevision);

        foreach (var token in RefreshTokens.Where(t => t.IsActive(now)))
        {
            token.Revoke(now);
        }

        Salt = newKdfSalt;
        EncryptedPrivateKey = newEncryptedPrivateKey;
        RecoverySalt = newRecoverySalt;
        EncryptedPrivateKeyByRecovery = newEncryptedPrivateKeyByRecovery;
        SecurityVersion = IdentityKdfProfiles.CurrentSecurityVersion;
        MinimumSecurityVersion = IdentityKdfProfiles.CurrentSecurityVersion;
        KdfProfileId = IdentityKdfProfiles.CurrentProfileId;
        CredentialRevision = rotatesPasswordCredential
            ? IncrementRevision(CredentialRevision)
            : CredentialRevision;
        PrivateKeyWrapRevision = IncrementRevision(PrivateKeyWrapRevision);
        DeviceWrapperMetadata = newDeviceWrapperMetadata;
        UpdatedAt = now;

        EmitAccountRecoveryCompleted();
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
        EnsureCurrentKdfProfile(SecurityVersion, KdfProfileId);
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

    private static void EnsureCurrentKdfProfile(ushort securityVersion, string? kdfProfileId)
    {
        if (securityVersion != IdentityKdfProfiles.CurrentSecurityVersion
            || kdfProfileId != IdentityKdfProfiles.CurrentProfileId)
        {
            throw new InvalidOperationException("The current registered Identity KDF profile is required.");
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
        if (PublicKey is not { Length: 32 })
        {
            throw new InvalidOperationException("The Member X25519 public key must contain exactly 32 raw bytes.");
        }

        if (MemberKeyVersion is null or 0)
        {
            throw new InvalidOperationException("The Member public key version must be established before publication.");
        }

        AddEvent(new UpsertMemberKeyDirectoryCommand(Id, MemberKeyVersion.Value, PublicKey.ToArray(), UpdatedAt));
    }

    private void EmitAccountRecoveryCompleted() =>
        AddEvent(new AccountRecoveryCompletedEvent(Id, OrganizationId, DisplayName, UpdatedAt));
}
