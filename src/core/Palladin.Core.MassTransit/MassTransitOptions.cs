using JetBrains.Annotations;

namespace Palladin.Core.MassTransit;

[PublicAPI]
public class MassTransitOptions
{
    public const string Position = "MassTransit";
    public RabbitMq RabbitMq { get; init; } = null!;
    public ConsumersOptions Consumers { get; init; } = new();
    public InMemoryOutboxOptions? InMemoryOutbox { get; init; }
    public KillSwitchOptions KillSwitch { get; init; } = new();
    public CircuitBreakerOptions CircuitBreaker { get; init; } = new();
    public RateLimiterOptions RateLimiter { get; init; } = new();
    public bool EnableDelayedMessageScheduler { get; init; } = true;
}

[PublicAPI]
public sealed class RabbitMq
{
    public string VirtualHost { get; init; } = null!;
    public string Host { get; init; } = null!;
    public string User { get; init; } = null!;
    public string Password { get; init; } = null!;
    public ushort Port { get; init; }
    public bool UseAmqps { get; init; } = true;
    public bool? PublisherConfirmation { get; init; }
    public TimeSpan? Heartbeat { get; init; }
    public ushort? RequestedChannelMax { get; init; }
    public TimeSpan? RequestedConnectionTimeout { get; init; }
    public TimeSpan? ContinuationTimeout { get; init; }
    public ICollection<string> Nodes { get; init; } = new List<string>();
    public string QueueDefaultType { get; init; } = "default";
}

[PublicAPI]
public sealed class ConsumersOptions
{
    public bool Disabled { get; init; }
    public ICollection<string> Blacklist { get; init; } = new List<string>();
    public RetriesOptions Retries { get; init; } = new();
}

[PublicAPI]
public sealed class InMemoryOutboxOptions
{
    public bool Disabled { get; init; }
}

[PublicAPI]
public sealed class KillSwitchOptions
{
    public bool Disabled { get; init; } = true;
    public int ActiveThresholdMessages { get; init; } = 10;
    public TimeSpan RestartAfter { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan TrackingPeriod { get; init; } = TimeSpan.FromMinutes(1);
    public int TripThreshold { get; init; } = 10;
}

[PublicAPI]
public sealed class CircuitBreakerOptions
{
    public bool Disabled { get; init; } = true;
    public int ActiveThresholdMessages { get; init; } = 10;
    public TimeSpan ResetInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan TrackingPeriod { get; init; } = TimeSpan.FromMinutes(5);
    public int TripThreshold { get; init; } = 15;
}

[PublicAPI]
public sealed class RateLimiterOptions
{
    public bool Disabled { get; init; } = true;
    public int RateLimit { get; init; } = 1000;
    public TimeSpan TrackingPeriod { get; init; } = TimeSpan.FromMinutes(1);
}

[PublicAPI]
public sealed class RetriesOptions
{
    public bool Disabled { get; init; }
    public ICollection<TimeSpan> Intervals { get; init; } = new List<TimeSpan>();
}
