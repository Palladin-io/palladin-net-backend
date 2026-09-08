using System.Net;

namespace Palladin.Module.Agents.Shared;

internal static class AgentConnectionInfo
{
    internal static string? NormalizeIp(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (IPAddress.IsLoopback(address))
        {
            return IPAddress.Loopback.ToString();
        }

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }
}
