using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class EntryShare : EventEntityBase
{
    internal const int NonceBytes = 24;
    internal const int MaximumCiphertextBytes = 262_144;

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid EntryId { get; private set; }
    public Guid CreatedBy { get; private set; }
    public uint SenderAuthorizationVersion { get; private set; }
    public Instant SenderVaultMembershipAddedAt { get; private set; }
    internal EntryRevision SourceRevision { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant ExpiresAt { get; private set; }
    public int MaximumReceipts { get; private set; }
    public int DeliveryCount { get; private set; }
    public Instant? FirstDeliveredAt { get; private set; }
    public Instant? LastDeliveredAt { get; private set; }
    public Instant? FirstConfirmedAt { get; private set; }
    public bool NotifyOnFirstReceipt { get; private set; }
    public EntryShareRecipientMode RecipientMode { get; private set; }
    public string? ProtectedRecipientEmail { get; private set; }
    public EntryShareProtection Protection { get; private set; }
    public string? SecretVerifier { get; private set; }
    public byte[] AccessTokenHash { get; private set; } = [];
    public byte[] Nonce { get; private set; } = [];
    public byte[] Ciphertext { get; private set; } = [];
    public long SecurityVersion { get; private set; }
    public long MutationVersion { get; private set; }
    public long ActivitySequence { get; private set; }
    public Instant? RevokedAt { get; private set; }
    public EntryShareActivityKind? RevocationReason { get; private set; }
    public Instant? ExpiredAt { get; private set; }
    public int FailedAttempts { get; private set; }
    public Instant? LockedUntil { get; private set; }
    public Instant? LastOtpSentAt { get; private set; }
    internal ICollection<EntryShareActivity> Activities { get; private set; } = [];

    private EntryShare() { }

    internal static EntryShare Create(
        Guid id,
        EntryScope source,
        EntryRevision sourceRevision,
        Guid createdBy,
        Instant now,
        Instant expiresAt,
        int maximumReceipts,
        EntryShareRecipientMode recipientMode,
        string? protectedRecipientEmail,
        EntryShareProtection protection,
        string? secretVerifier,
        byte[] accessTokenHash,
        byte[] nonce,
        byte[] ciphertext,
        bool notifyOnFirstReceipt,
        uint senderAuthorizationVersion,
        Instant senderVaultMembershipAddedAt)
    {
        source.Validate();
        if (id == Guid.Empty || createdBy == Guid.Empty || sourceRevision.Value == 0 || senderAuthorizationVersion == 0
            || expiresAt <= now || maximumReceipts < 1
            || accessTokenHash.Length != 32 || nonce.Length != NonceBytes
            || ciphertext.Length is < 16 or > MaximumCiphertextBytes)
        {
            throw new DomainException("The encrypted sharing snapshot, identity and limits must be valid.");
        }

        if (!Enum.IsDefined(recipientMode)
            || (recipientMode == EntryShareRecipientMode.NamedRecipient
                ? string.IsNullOrWhiteSpace(protectedRecipientEmail)
                : protectedRecipientEmail is not null))
        {
            throw new DomainException("The sharing recipient policy must be explicit.");
        }

        ValidateProtection(protection, secretVerifier);
        var share = new EntryShare
        {
            Id = id,
            OrganizationId = source.OrganizationId,
            VaultId = source.VaultId,
            EntryId = source.EntryId,
            SourceRevision = sourceRevision,
            CreatedBy = createdBy,
            SenderAuthorizationVersion = senderAuthorizationVersion,
            SenderVaultMembershipAddedAt = senderVaultMembershipAddedAt,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            MaximumReceipts = maximumReceipts,
            RecipientMode = recipientMode,
            ProtectedRecipientEmail = protectedRecipientEmail,
            Protection = protection,
            SecretVerifier = secretVerifier,
            AccessTokenHash = accessTokenHash.ToArray(),
            Nonce = nonce.ToArray(),
            Ciphertext = ciphertext.ToArray(),
            NotifyOnFirstReceipt = notifyOnFirstReceipt,
            SecurityVersion = 1,
            MutationVersion = 1,
        };
        share.Record(EntryShareActivityKind.Created, now);
        return share;
    }

    internal EntryShareSession OpenSession(Guid sessionId, byte[] tokenHash, Instant now, Duration lifetime)
    {
        EnsureAvailable(now);
        if (DeliveryCount >= MaximumReceipts || lifetime <= Duration.Zero)
        {
            throw new EntryShareUnavailableException();
        }

        var end = now + lifetime;
        AdvanceMutation();
        return EntryShareSession.Create(Id, sessionId, tokenHash, SecurityVersion, now,
            end < ExpiresAt ? end : ExpiresAt);
    }

    internal int OtpRetryAfterSeconds(Instant now, Duration cooldown) => LastOtpSentAt is { } last
        ? (int)Math.Max(0, Math.Ceiling((last + cooldown - now).TotalSeconds))
        : 0;

    internal void IssueOtp(
        EntryShareSession session, byte[] otpHash, Instant now, Duration lifetime, Duration cooldown,
        EntryShareOtpDelivery delivery)
    {
        EnsureSession(session, now);
        if (RecipientMode != EntryShareRecipientMode.NamedRecipient || DeliveryCount >= MaximumReceipts || lifetime <= Duration.Zero
            || cooldown <= Duration.Zero || (LastOtpSentAt is { } last && now < last + cooldown))
        {
            throw new EntryShareUnavailableException();
        }

        var end = now + lifetime;
        session.IssueOtp(otpHash, end < session.ExpiresAt ? end : session.ExpiresAt, delivery, now);
        LastOtpSentAt = now;
        AdvanceMutation();
    }

    internal void VerifyEmail(EntryShareSession session, Instant now)
    {
        EnsureSession(session, now);
        session.VerifyEmail(now);
        AdvanceMutation();
    }

    internal void VerifySecret(EntryShareSession session, Instant now)
    {
        EnsureSession(session, now);
        if (Protection == EntryShareProtection.None)
        {
            throw new EntryShareUnavailableException();
        }

        session.VerifySecret(now);
        AdvanceMutation();
    }

    internal bool Deliver(EntryShareSession session, Instant now)
    {
        EnsureAuthorizedSession(session, now);
        if (session.DeliveredAt is not null)
        {
            AdvanceMutation();
            return false;
        }

        if (DeliveryCount >= MaximumReceipts)
        {
            throw new EntryShareUnavailableException();
        }

        session.MarkDelivered(now);
        DeliveryCount = checked(DeliveryCount + 1);
        FirstDeliveredAt ??= now;
        LastDeliveredAt = now;
        AdvanceMutation();
        Record(EntryShareActivityKind.Delivered, now);
        return true;
    }

    internal bool ConfirmReceipt(EntryShareSession session, Instant now)
    {
        EnsureAuthorizedSession(session, now);
        if (!session.Confirm(now))
        {
            return false;
        }

        var first = FirstConfirmedAt is null;
        FirstConfirmedAt ??= now;
        AdvanceMutation();
        Record(EntryShareActivityKind.Confirmed, now, first);
        return true;
    }

    internal void ChangeProtection(EntryShareProtection protection, string? secretVerifier, Instant now)
    {
        EnsureAvailable(now);
        ValidateProtection(protection, secretVerifier);
        Protection = protection;
        SecretVerifier = secretVerifier;
        SecurityVersion = checked(SecurityVersion + 1);
        AdvanceMutation();
        Record(EntryShareActivityKind.ProtectionChanged, now);
    }

    internal void RegisterFailedAttempt(Instant now, int attemptLimit, int totalAttemptLimit, Duration lockout)
    {
        EnsureAvailable(now);
        if (attemptLimit < 1 || totalAttemptLimit < attemptLimit || lockout <= Duration.Zero)
        {
            throw new DomainException("Sharing attempt limits must be finite and positive.");
        }

        FailedAttempts = checked(FailedAttempts + 1);
        if (FailedAttempts >= totalAttemptLimit)
        {
            LockedUntil = ExpiresAt;
        }
        else if (FailedAttempts % attemptLimit == 0)
        {
            LockedUntil = now + lockout;
        }

        AdvanceMutation();
    }

    internal bool EndByRecipient(EntryShareSession session, Instant now)
    {
        EnsureAuthorizedSession(session, now);
        return Revoke(EntryShareActivityKind.EndedByRecipient, now);
    }

    internal bool Revoke(EntryShareActivityKind reason, Instant now)
    {
        if (reason is not (EntryShareActivityKind.RevokedBySender
            or EntryShareActivityKind.EndedByRecipient or EntryShareActivityKind.SourceAccessRemoved))
        {
            throw new DomainException("The sharing revocation reason is invalid.");
        }

        if (RevokedAt is not null || ExpiredAt is not null)
        {
            return false;
        }

        RevokedAt = now;
        RevocationReason = reason;
        EraseDeliveryMaterial();
        AdvanceMutation();
        Record(reason, now);
        return true;
    }

    internal bool Expire(Instant now)
    {
        if (now < ExpiresAt || RevokedAt is not null || ExpiredAt is not null)
        {
            return false;
        }

        ExpiredAt = now;
        EraseDeliveryMaterial();
        AdvanceMutation();
        Record(EntryShareActivityKind.Expired, now);
        return true;
    }

    internal void EnsureAuthorizedSession(EntryShareSession session, Instant now)
    {
        EnsureSession(session, now);
        if ((RecipientMode == EntryShareRecipientMode.NamedRecipient && session.EmailVerifiedAt is null)
            || (Protection != EntryShareProtection.None && session.SecretVerifiedAt is null))
        {
            throw new EntryShareUnavailableException();
        }
    }

    internal IReadOnlyList<EntryShareActivity> FetchUnstagedActivities()
    {
        var activities = Activities.ToArray();
        Activities.Clear();
        return activities;
    }

    internal void EnsureSession(EntryShareSession session, Instant now)
    {
        EnsureAvailable(now);
        if (session.ShareId != Id || session.SecurityVersion != SecurityVersion
            || now >= session.ExpiresAt || now < session.CreatedAt)
        {
            throw new EntryShareUnavailableException();
        }
    }

    private void EnsureAvailable(Instant now)
    {
        if (RevokedAt is not null || now >= ExpiresAt || now < CreatedAt
            || (LockedUntil is { } lockedUntil && now < lockedUntil))
        {
            throw new EntryShareUnavailableException();
        }
    }

    private static void ValidateProtection(EntryShareProtection protection, string? verifier)
    {
        if (!Enum.IsDefined(protection)
            || (protection == EntryShareProtection.None ? verifier is not null : string.IsNullOrWhiteSpace(verifier)))
        {
            throw new DomainException("Sharing protection requires exactly its selected verifier.");
        }
    }

    private void EraseDeliveryMaterial()
    {
        Ciphertext = [];
        Nonce = [];
        AccessTokenHash = [];
        SecretVerifier = null;
        ProtectedRecipientEmail = null;
        SecurityVersion = checked(SecurityVersion + 1);
    }

    private void AdvanceMutation() => MutationVersion = checked(MutationVersion + 1);

    private void Record(EntryShareActivityKind kind, Instant now, bool firstConfirmation = true)
    {
        ActivitySequence = checked(ActivitySequence + 1);
        var activity = EntryShareActivity.Create(this, kind, now, firstConfirmation);
        Activities.Add(activity);
        AddEvent(activity.ToEvent());
    }
}
