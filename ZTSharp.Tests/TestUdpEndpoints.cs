using System.Net;

namespace ZTSharp.Tests;

internal static class TestUdpEndpoints
{
    public static IPEndPoint ToLoopback(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.Address.IsIPv4MappedToIPv6)
        {
            endpoint = new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port);
        }

        if (endpoint.Address.Equals(IPAddress.Any))
        {
            return new IPEndPoint(IPAddress.Loopback, endpoint.Port);
        }

        if (endpoint.Address.Equals(IPAddress.IPv6Any))
        {
            return new IPEndPoint(IPAddress.IPv6Loopback, endpoint.Port);
        }

        return endpoint;
    }
}

