using System.Net;

namespace ZTSharp.Transport.Internal;

internal static class UdpEndpointNormalization
{
    public static IPEndPoint Normalize(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.Address.IsIPv4MappedToIPv6)
        {
            return new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port);
        }

        return endpoint;
    }

    public static IPEndPoint NormalizeForAdvertisement(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var normalized = Normalize(endpoint);
        if (normalized.Address.Equals(IPAddress.Any))
        {
            return new IPEndPoint(IPAddress.Loopback, normalized.Port);
        }

        if (normalized.Address.Equals(IPAddress.IPv6Any))
        {
            return new IPEndPoint(IPAddress.IPv6Loopback, normalized.Port);
        }

        return normalized;
    }

    public static void ValidateRemoteEndpoint(IPEndPoint endpoint, string paramName)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(paramName);

        if (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))
        {
            throw new ArgumentException("Remote endpoint must use a concrete address (not IPAddress.Any/IPv6Any).", paramName);
        }
    }
}
