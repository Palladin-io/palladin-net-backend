using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal enum SharedUnlockLinkState
{
    Locked = 1,
    Active = 2,
    Revoked = 3,
}

internal sealed class SharedUnlockLink
{
    public Guid UserId { get; private set; }
    public Guid Id { get; private set; }
    public SharedUnlockLinkState State { get; private set; }
    public uint Epoch { get; private set; }
    public uint Revision { get; private set; }
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

    internal bool TryLock(uint expectedRevision, Instant now) =>
        State != SharedUnlockLinkState.Revoked && TryTransition(SharedUnlockLinkState.Locked, expectedRevision, now);

    internal bool TryDisconnect(uint expectedRevision, Instant now) =>
        TryTransition(SharedUnlockLinkState.Revoked, expectedRevision, now);

    internal bool TryReconnect(uint expectedRevision, Instant now) =>
        State == SharedUnlockLinkState.Revoked && TryTransition(SharedUnlockLinkState.Locked, expectedRevision, now);

    internal bool TryActivateFromManualUnlock(uint expectedRevision, Instant now) =>
        State != SharedUnlockLinkState.Revoked && TryTransition(SharedUnlockLinkState.Active, expectedRevision, now);

    internal bool AllowsTransfer(uint epoch) => State == SharedUnlockLinkState.Active && Epoch == epoch;

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
