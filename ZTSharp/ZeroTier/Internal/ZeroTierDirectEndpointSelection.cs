using System.Net;
using System.Net.Sockets;
using System.Globalization;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierDirectEndpointSelection
{
    public static IPEndPoint[] Normalize(IEnumerable<IPEndPoint> endpoints, IPEndPoint relayEndpoint, int maxEndpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(relayEndpoint);

        var publicV4 = new List<IPEndPoint>();
        var publicV6 = new List<IPEndPoint>();
        var privateV4 = new List<IPEndPoint>();
        var privateV6 = new List<IPEndPoint>();

        foreach (var endpoint in endpoints)
        {
            if (endpoint.Port is < 1 or > ushort.MaxValue)
            {
                continue;
            }

            if (endpoint.Equals(relayEndpoint))
            {
                continue;
            }

            if (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))
            {
                continue;
            }

            var isPublic = IsPublicAddress(endpoint.Address);
            if (endpoint.AddressFamily == AddressFamily.InterNetwork)
            {
                (isPublic ? publicV4 : privateV4).Add(endpoint);
            }
            else if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                (isPublic ? publicV6 : privateV6).Add(endpoint);
            }
        }

        var ordered = publicV4
            .Concat(publicV6)
            .Concat(privateV4)
            .Concat(privateV6);

        var unique = new List<IPEndPoint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in ordered)
        {
            var keyAddress = endpoint.Address;
            if (keyAddress.AddressFamily == AddressFamily.InterNetworkV6 && keyAddress.IsIPv4MappedToIPv6)
            {
                keyAddress = keyAddress.MapToIPv4();
            }

            var key = keyAddress + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
            if (!seen.Add(key))
            {
                continue;
            }

            unique.Add(endpoint);
            if (unique.Count >= maxEndpoints)
            {
                break;
            }
        }

        return unique.ToArray();
    }

    public static string Format(IPEndPoint[] endpoints)
    {
        if (endpoints.Length == 0)
        {
            return "<none>";
        }

        return string.Join(", ", endpoints.Select(endpoint => endpoint.ToString()));
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
            {
                return false;
            }

            if (bytes[0] == 10)
            {
                return false;
            }

            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            {
                return false;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return false;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return false;
            }

            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            {
                return false;
            }

            if (bytes[0] == 0 || bytes[0] >= 224)
            {
                return false;
            }

            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            {
                return false;
            }

            if (address.IsIPv6LinkLocal ||
                address.IsIPv6Multicast ||
                address.IsIPv6SiteLocal ||
                address.Equals(IPAddress.IPv6Loopback))
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            if (bytes.Length != 16)
            {
                return false;
            }

            // fc00::/7 Unique Local Address (ULA)
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return false;
            }

            return true;
        }

        return false;
    }
}

