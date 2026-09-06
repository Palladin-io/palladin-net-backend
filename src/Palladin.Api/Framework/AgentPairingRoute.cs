namespace Palladin.Api.Framework;

internal static class AgentPairingRoute
{
    private const string StartPath = "/api/agent-pairings";
    private const string StatusSuffix = "/status";

    internal static bool IsStart(PathString path)
    {
        var value = path.Value;
        return value is not null
            && string.Equals(value.TrimEnd('/'), StartPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsStatus(PathString path)
    {
        var value = path.Value?.TrimEnd('/');
        if (value is null
            || !value.StartsWith(StartPath + '/', StringComparison.OrdinalIgnoreCase)
            || !value.EndsWith(StatusSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pairingIdStart = StartPath.Length + 1;
        var pairingIdLength = value.Length - pairingIdStart - StatusSuffix.Length;
        if (pairingIdLength <= 0)
        {
            return false;
        }

        var pairingId = value.Substring(pairingIdStart, pairingIdLength);
        // FastEndpoints binds the route parameter as Guid and therefore accepts
        // every representation supported by Guid.TryParse, including the compact
        // N form. The limiter must classify the exact same request set.
        return Guid.TryParse(pairingId, out _);
    }
}
