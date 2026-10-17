namespace Palladin.Core.Options;

public sealed class NetworkingOptions
{
    public const string Position = "Networking";

    public bool TrustForwardedHeaders { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public string[] AllowedOrigins { get; init; } = [];

    public void ValidateAllowedOrigins()
    {
        if (AllowedOrigins.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
                || value.Contains('*') || uri.GetLeftPart(UriPartial.Authority) != value))
        {
            throw new InvalidOperationException("Networking:AllowedOrigins requires exact HTTP or HTTPS origins.");
        }
    }

    // Individual proxy IPs whose X-Forwarded-* headers we trust.
    public string[] KnownProxies { get; init; } = [];

    // Trusted proxy subnets in CIDR notation (e.g. "10.0.0.0/8").
    public string[] KnownNetworks { get; init; } = [];
}
