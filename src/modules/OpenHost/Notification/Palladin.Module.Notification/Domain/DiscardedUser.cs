using NodaTime;

namespace Palladin.Module.Notification.Domain;

// Permanent fence for a user whose unverified Identity account was discarded. Registration-time upserts can
// arrive after the discard on other queues; they must not recreate replicas of a user that no longer exists.
internal sealed class DiscardedUser
{
    public Guid UserId { get; private set; }
    public Instant DiscardedAt { get; private set; }

    private DiscardedUser() { }

    internal static DiscardedUser Create(Guid userId, Instant discardedAt) =>
        new() { UserId = userId, DiscardedAt = discardedAt };
}
