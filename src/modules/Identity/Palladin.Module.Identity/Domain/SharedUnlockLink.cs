using NodaTime;
using Palladin.Core.Events;
using Palladin.Module.Identity.Contracts.Events;

namespace Palladin.Module.Identity.Domain;

internal enum SharedUnlockLinkState
{
    Locked = 1,
    Active = 2,
    Revoked = 3,
}

internal sealed class SharedUnlockLink : EventEntityBase
{
    public Guid UserId { get; private set; }
    public Guid Id { get; private set; }
    public SharedUnlockLinkState State { get; private set; }
    public uint Epoch { get; private set; }
    public uint Revision { get; private set; }
    public uint LastInvalidationSequence { get; private set; }
    public uint LastLogoutSequence { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private SharedUnlockLink() { }

    internal static SharedUnlockLink Create(Guid userId, Guid id, Instant now)
    {
        if (userId == Guid.Empty || id == Guid.Empty)
        {
            throw new ArgumentException("Shared unlock link identifiers must be nonempty.");
        }

        return new SharedUnlockLink
        {
            UserId = userId,
            Id = id,
            State = SharedUnlockLinkState.Locked,
            Epoch = 1,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    internal bool TryLock(uint expectedRevision, uint sequence, Instant now) =>
        State != SharedUnlockLinkState.Revoked && TryInvalidate(SharedUnlockLinkState.Locked, expectedRevision, sequence, now);

    internal bool TryLogout(uint expectedRevision, uint sequence, Instant now)
    {
        if (!TryLock(expectedRevision, sequence, now))
        {
            return false;
        }
        LastLogoutSequence = sequence;
        AddEvent(new UserLoggedOutEvent(UserId));
        return true;
    }

    internal bool RevokesSession(SharedUnlockAuthorization authorization) =>
        authorization.UserId == UserId && authorization.LinkId == Id
        && authorization.Sequence <= LastLogoutSequence;

    internal bool TryDisconnect(uint expectedRevision, uint sequence, Instant now) =>
        TryInvalidate(SharedUnlockLinkState.Revoked, expectedRevision, sequence, now);

    internal bool TryReconnect(uint expectedRevision, uint sequence, Instant now) =>
        State == SharedUnlockLinkState.Revoked && TryInvalidate(SharedUnlockLinkState.Locked, expectedRevision, sequence, now);

    internal bool TryActivateFromManualUnlock(uint expectedRevision, uint authorizationSequence, Instant now) =>
        authorizationSequence > LastInvalidationSequence
        && State != SharedUnlockLinkState.Revoked && TryTransition(SharedUnlockLinkState.Active, expectedRevision, now);

    internal bool AllowsTransfer(uint epoch) => State == SharedUnlockLinkState.Active && Epoch == epoch;

    private bool TryInvalidate(SharedUnlockLinkState state, uint expectedRevision, uint sequence, Instant now)
    {
        if (sequence <= LastInvalidationSequence || !TryTransition(state, expectedRevision, now))
        {
            return false;
        }

        LastInvalidationSequence = sequence;
        return true;
    }

    private bool TryTransition(SharedUnlockLinkState state, uint expectedRevision, Instant now)
    {
        if (Revision != expectedRevision || Revision == uint.MaxValue || Epoch == uint.MaxValue)
        {
            return false;
        }

        State = state;
        Revision++;
        Epoch++;
        UpdatedAt = now;
        return true;
    }
}
