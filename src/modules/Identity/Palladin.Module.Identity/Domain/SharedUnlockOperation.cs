using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal enum SharedUnlockDirection { WebToExtension = 1, ExtensionToWeb = 2 }
internal enum SharedUnlockOperationState { Offered = 1, Consumed = 2, Committed = 3 }

internal sealed record SharedUnlockChannel(SharedUnlockDirection Direction, string ApiOrigin,
    string WebOrigin, string ExtensionId, string DocumentBinding, byte[] WebGeneration,
    byte[] ExtensionGeneration, byte[] SourcePublicKey, byte[] RecipientPublicKey,
    byte[] RecipientProofPublicKey);

internal sealed class SharedUnlockOperation
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid SourceSessionId { get; private set; }
    public Guid SourceRefreshTokenId { get; private set; }
    public Guid SourceAuthorizationId { get; private set; }
    public uint SourceSequence { get; private set; }
    public Guid SourceOrganizationId { get; private set; }
    public uint SourceOfflinePolicyVersion { get; private set; }
    public Guid OrganizationId { get; private set; }
    public uint OfflinePolicyVersion { get; private set; }
    public uint AuthorizationVersion { get; private set; }
    public Guid LinkId { get; private set; }
    public uint LinkEpoch { get; private set; }
    public uint PreferenceRevision { get; private set; }
    public SharedUnlockDirection Direction { get; private set; }
    public string ApiOrigin { get; private set; } = string.Empty;
    public string WebOrigin { get; private set; } = string.Empty;
    public string ExtensionId { get; private set; } = string.Empty;
    public string DocumentBinding { get; private set; } = string.Empty;
    public byte[] WebGeneration { get; private set; } = [];
    public byte[] ExtensionGeneration { get; private set; } = [];
    public byte[] SourcePublicKey { get; private set; } = [];
    public byte[] RecipientPublicKey { get; private set; } = [];
    public byte[] RecipientProofPublicKey { get; private set; } = [];
    public byte[] Challenge { get; private set; } = [];
    public byte[] KeyContextDigest { get; private set; } = [];
    public byte[] TranscriptHash { get; private set; } = [];
    public Instant IssuedAt { get; private set; }
    public Instant ExpiresAt { get; private set; }
    public Instant UnlockedAt { get; private set; }
    public Instant IdleDeadline { get; private set; }
    public Instant AbsoluteDeadline { get; private set; }
    public Instant OfflineDeadline { get; private set; }
    public SharedUnlockOperationState State { get; private set; }
    public uint Revision { get; private set; }
    public Guid? RecipientSessionId { get; private set; }

    private SharedUnlockOperation() { }

    internal static SharedUnlockOperation Create(Guid id, User user,
        SharedUnlockAuthorization source, RefreshToken sourceSession,
        Organization sourceOrganization, Organization targetOrganization,
        OrganizationMember targetMember, SharedUnlockLink link,
        SharedUnlockChannel channel, byte[] keyDigest, byte[] challenge, Instant idleDeadline,
        Instant absoluteDeadline, Instant offlineDeadline, Instant now)
    {
        absoluteDeadline = new[] { absoluteDeadline, source.AbsoluteDeadline, sourceSession.ExpiresAt }.Min();
        idleDeadline = new[] { idleDeadline, source.IdleDeadline, absoluteDeadline }.Min();
        offlineDeadline = new[] { offlineDeadline, source.OfflineDeadline, sourceSession.ExpiresAt }.Min();
        var expiresAt = new[] { now + Duration.FromSeconds(30), idleDeadline,
            absoluteDeadline, offlineDeadline }.Min();
        expiresAt = Instant.FromUnixTimeMilliseconds(expiresAt.ToUnixTimeMilliseconds());
        if (expiresAt <= now || id == Guid.Empty || keyDigest.Length != 32 || challenge.Length != 32
            || source.UserId != user.Id || sourceSession.UserId != user.Id || link.UserId != user.Id
            || source.SessionId != (sourceSession.SessionId ?? sourceSession.Id)
            || source.LinkId != link.Id || source.LinkEpoch != link.Epoch || !link.AllowsTransfer(link.Epoch)
            || source.OrganizationId != sourceOrganization.Id || targetMember.UserId != user.Id
            || targetMember.OrganizationId != targetOrganization.Id
            || targetMember.Status != OrganizationMemberStatus.Active)
        {
            throw new ArgumentException("Invalid shared unlock operation authority.");
        }

        return new SharedUnlockOperation
        {
            Id = id, UserId = user.Id, SourceSessionId = source.SessionId,
            SourceRefreshTokenId = sourceSession.Id, SourceAuthorizationId = source.Id,
            SourceSequence = source.Sequence, SourceOrganizationId = sourceOrganization.Id,
            SourceOfflinePolicyVersion = sourceOrganization.OfflineAccessPolicyVersion,
            OrganizationId = targetOrganization.Id, OfflinePolicyVersion = targetOrganization.OfflineAccessPolicyVersion,
            AuthorizationVersion = targetMember.AuthorizationVersion, LinkId = link.Id,
            LinkEpoch = link.Epoch, PreferenceRevision = user.SharedUnlockRevision,
            Direction = channel.Direction, ApiOrigin = channel.ApiOrigin, WebOrigin = channel.WebOrigin,
            ExtensionId = channel.ExtensionId, DocumentBinding = channel.DocumentBinding,
            WebGeneration = channel.WebGeneration.ToArray(), ExtensionGeneration = channel.ExtensionGeneration.ToArray(),
            SourcePublicKey = channel.SourcePublicKey.ToArray(), RecipientPublicKey = channel.RecipientPublicKey.ToArray(),
            RecipientProofPublicKey = channel.RecipientProofPublicKey.ToArray(),
            Challenge = challenge.ToArray(), KeyContextDigest = keyDigest.ToArray(),
            IssuedAt = now, ExpiresAt = expiresAt, UnlockedAt = source.UnlockedAt,
            IdleDeadline = idleDeadline, AbsoluteDeadline = absoluteDeadline,
            OfflineDeadline = offlineDeadline, State = SharedUnlockOperationState.Offered, Revision = 1,
        };
    }

    internal void BindTranscriptHash(byte[] hash)
    {
        if (TranscriptHash.Length != 0 || hash.Length != 32)
        {
            throw new InvalidOperationException("The operation transcript is immutable.");
        }
        TranscriptHash = hash.ToArray();
    }

    internal bool TryConsume(Instant now)
    {
        if (State != SharedUnlockOperationState.Offered || now < IssuedAt || now >= ExpiresAt)
        {
            return false;
        }
        State = SharedUnlockOperationState.Consumed;
        Revision++;
        return true;
    }

    internal bool TryCommit(Guid recipientSessionId, Instant now)
    {
        if (recipientSessionId == Guid.Empty || State != SharedUnlockOperationState.Consumed
            || now < IssuedAt || now >= ExpiresAt)
        {
            return false;
        }
        State = SharedUnlockOperationState.Committed;
        RecipientSessionId = recipientSessionId;
        Revision++;
        return true;
    }

    internal byte[] SourceGeneration => Direction == SharedUnlockDirection.WebToExtension ? WebGeneration : ExtensionGeneration;
    internal byte[] RecipientGeneration => Direction == SharedUnlockDirection.WebToExtension ? ExtensionGeneration : WebGeneration;
}
