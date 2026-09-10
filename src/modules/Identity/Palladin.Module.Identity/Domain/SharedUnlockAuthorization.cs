using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class SharedUnlockAuthorization
{
    public Guid UserId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public uint Sequence { get; private set; }
    public Guid? LinkId { get; private set; }
    public uint? LinkEpoch { get; private set; }
    public byte[] SourceGeneration { get; private set; } = [];
    public uint CredentialRevision { get; private set; }
    public uint PrivateKeyWrapRevision { get; private set; }
    public uint AuthorizationVersion { get; private set; }
    public uint? SecondFactorRevision { get; private set; }
    public Instant? SecondFactorVerifiedAt { get; private set; }
    public Instant UnlockedAt { get; private set; }
    public Instant IdleDeadline { get; private set; }
    public Instant AbsoluteDeadline { get; private set; }
    public Instant OfflineDeadline { get; private set; }

    private SharedUnlockAuthorization() { }

    internal static SharedUnlockAuthorization Create(Guid userId, Guid sessionId) =>
        new() { UserId = userId, SessionId = sessionId };

    internal void AuthorizeManualUnlock(Guid id, User user, RefreshToken session,
        byte[] sourceGeneration, Instant idleDeadline, Instant absoluteDeadline,
        Instant offlineDeadline, Instant now)
    {
        if (id == Guid.Empty || user.Id != UserId || session.UserId != UserId
            || (session.SessionId ?? session.Id) != SessionId
            || !session.IsActive(now) || user.SharedUnlockSequence == 0
            || sourceGeneration.Length != 32 || !user.IsOnboarded
            || !ValidDeadlines(idleDeadline, absoluteDeadline, offlineDeadline, session.ExpiresAt, now))
        {
            throw new ArgumentException("Invalid manual unlock authority.");
        }

        Id = id;
        OrganizationId = session.OrganizationId;
        Sequence = user.SharedUnlockSequence;
        LinkId = null;
        LinkEpoch = null;
        SourceGeneration = sourceGeneration.ToArray();
        CredentialRevision = user.CredentialRevision;
        PrivateKeyWrapRevision = user.PrivateKeyWrapRevision;
        AuthorizationVersion = session.AuthorizationVersion;
        SecondFactorRevision = session.SecondFactorRevision;
        SecondFactorVerifiedAt = session.SecondFactorVerifiedAt;
        UnlockedAt = Instant.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        IdleDeadline = idleDeadline;
        AbsoluteDeadline = absoluteDeadline;
        OfflineDeadline = offlineDeadline;
    }

    internal static SharedUnlockAuthorization Inherit(Guid id, RefreshToken receiver,
        SharedUnlockAuthorization source, SharedUnlockOperation operation)
    {
        if (id == Guid.Empty || receiver.UserId != operation.UserId || source.UserId != operation.UserId
            || source.Id != operation.SourceAuthorizationId || source.SessionId != operation.SourceSessionId
            || source.Sequence != operation.SourceSequence || source.LinkId != operation.LinkId
            || source.LinkEpoch != operation.LinkEpoch || receiver.OrganizationId != operation.OrganizationId
            || receiver.AuthorizationVersion != operation.AuthorizationVersion
            || receiver.SecondFactorRevision != source.SecondFactorRevision
            || receiver.SecondFactorVerifiedAt != source.SecondFactorVerifiedAt)
        {
            throw new ArgumentException("Invalid inherited unlock authority.");
        }
        return new SharedUnlockAuthorization
        {
            Id = id, UserId = receiver.UserId, SessionId = receiver.SessionId ?? receiver.Id,
            OrganizationId = receiver.OrganizationId, Sequence = operation.SourceSequence,
            LinkId = operation.LinkId, LinkEpoch = operation.LinkEpoch,
            SourceGeneration = operation.RecipientGeneration.ToArray(),
            CredentialRevision = source.CredentialRevision, PrivateKeyWrapRevision = source.PrivateKeyWrapRevision,
            AuthorizationVersion = receiver.AuthorizationVersion,
            SecondFactorRevision = source.SecondFactorRevision, SecondFactorVerifiedAt = source.SecondFactorVerifiedAt,
            UnlockedAt = operation.UnlockedAt, IdleDeadline = operation.IdleDeadline,
            AbsoluteDeadline = operation.AbsoluteDeadline, OfflineDeadline = operation.OfflineDeadline,
        };
    }

    internal bool TryBind(SharedUnlockLink link)
    {
        if (link.UserId != UserId || !link.AllowsTransfer(link.Epoch)
            || Sequence <= link.LastInvalidationSequence
            || (LinkId is not null && (LinkId != link.Id || LinkEpoch != link.Epoch)))
        {
            return false;
        }

        LinkId = link.Id;
        LinkEpoch = link.Epoch;
        return true;
    }

    internal bool IsCurrent(User user, RefreshToken session, OrganizationMember membership,
        TotpCredential? factor, Instant now) =>
        user.Id == UserId && user.SharedUnlockEnabled
        && user.CredentialRevision == CredentialRevision
        && user.PrivateKeyWrapRevision == PrivateKeyWrapRevision
        && session.UserId == UserId && (session.SessionId ?? session.Id) == SessionId
        && session.OrganizationId == OrganizationId
        && session.AuthorizationVersion == AuthorizationVersion
        && membership.UserId == UserId && membership.OrganizationId == OrganizationId
        && membership.Status == OrganizationMemberStatus.Active
        && membership.AuthorizationVersion == AuthorizationVersion
        && session.SatisfiesSecondFactor(factor, now)
        && (factor is not { IsEnabled: true } || SecondFactorRevision == factor.ConfigurationRevision)
        && now >= UnlockedAt && now < IdleDeadline && now < AbsoluteDeadline && now < OfflineDeadline;

    internal static bool ValidDeadlines(Instant idle, Instant absolute, Instant offline,
        Instant sessionExpiry, Instant now) =>
        idle > now && absolute > now && offline > now
        && idle <= absolute && absolute <= sessionExpiry && offline <= sessionExpiry;
}
