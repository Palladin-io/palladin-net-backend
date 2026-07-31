namespace Palladin.Core.Options;

public sealed class NetworkingOptions
{
    public const string Position = "Networking";

    public bool TrustForwardedHeaders { get; init; }

    // Individual proxy IPs whose X-Forwarded-* headers we trust.
    public string[] KnownProxies { get; init; } = [];

    // Trusted proxy subnets in CIDR notation (e.g. "10.0.0.0/8").
    public string[] KnownNetworks { get; init; } = [];
}
