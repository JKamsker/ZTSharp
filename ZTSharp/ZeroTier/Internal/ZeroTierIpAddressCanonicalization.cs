using System.Net;
using System.Net.Sockets;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierIpAddressCanonicalization
{
    public static IPAddress CanonicalizeForManagedIpComparison(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        // ScopeId is only meaningful for link-local addresses. For other IPv6 addresses,
        // treat ScopeId mismatches as equivalent for managed-IP comparisons.
        if (address.IsIPv6LinkLocal)
        {
            return address;
        }

        if (address.ScopeId == 0)
        {
            return address;
        }

        return new IPAddress(address.GetAddressBytes(), 0);
    }

    public static bool EqualsForManagedIpComparison(IPAddress left, IPAddress right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.AddressFamily != right.AddressFamily)
        {
            return false;
        }

        if (left.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return left.Equals(right);
        }

        var canonicalLeft = CanonicalizeForManagedIpComparison(left);
        var canonicalRight = CanonicalizeForManagedIpComparison(right);
        return canonicalLeft.Equals(canonicalRight);
    }
}
