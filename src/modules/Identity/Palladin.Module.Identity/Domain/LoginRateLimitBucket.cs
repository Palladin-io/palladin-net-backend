using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class LoginRateLimitBucket
{
    public Guid Id { get; private set; }
    public string PartitionKey { get; private set; } = string.Empty;
    public int RequestCount { get; private set; }
    public Instant WindowStartedAt { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    private LoginRateLimitBucket() { }

    internal static LoginRateLimitBucket Create(Guid id, string partitionKey, Instant now) =>
        new()
        {
            Id = id,
            PartitionKey = partitionKey,
            WindowStartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal LoginRateLimitDecision TryAcquire(int permitLimit, Duration window, Instant now)
    {
        var windowEndsAt = WindowStartedAt + window;
        if (now >= windowEndsAt)
        {
            RequestCount = 0;
            WindowStartedAt = now;
            windowEndsAt = now + window;
        }

        if (RequestCount >= permitLimit)
        {
            return LoginRateLimitDecision.Rejected(windowEndsAt);
        }

        RequestCount++;
        UpdatedAt = now;
        Version++;
        return LoginRateLimitDecision.Acquired(windowEndsAt);
    }
}

internal readonly record struct LoginRateLimitDecision(bool IsAcquired, Instant WindowEndsAt)
{
    internal static LoginRateLimitDecision Acquired(Instant windowEndsAt) => new(true, windowEndsAt);

    internal static LoginRateLimitDecision Rejected(Instant windowEndsAt) => new(false, windowEndsAt);
}
