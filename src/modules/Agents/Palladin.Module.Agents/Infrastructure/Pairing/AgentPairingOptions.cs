using System.Net;

namespace Palladin.Module.Agents.Infrastructure.Pairing;

internal sealed class AgentPairingOptions
{
    public const string Position = "Modules:Agents:Pairing";

    public string ApprovalUrlBase { get; init; } = string.Empty;
    // Must remain below the native runtime's pairing-only 31-minute authorization lease so an
    // approvable server request cannot outlive the local operation that can consume it.
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(30);
    internal bool HasSupportedLifetime => Lifetime == TimeSpan.FromMinutes(30);
    public int PollIntervalMilliseconds { get; init; } = 2_000;

    internal static bool IsValidApprovalUrlBase(string value)
    {
        var normalized = value.TrimEnd('/');
        if (normalized is "https://palladin.io" or "https://stage.palladin.io")
        {
            return true;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && !uri.IsDefaultPort
            && IPAddress.TryParse(uri.Host, out var address)
            && IPAddress.IsLoopback(address)
            && uri.AbsolutePath == "/"
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}
