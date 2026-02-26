using System.Net;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class ZeroTierTcpRouteKeyV6Tests
{
    [Fact]
    public void FromAddresses_ThrowsOnScopedIpv6()
    {
        var linkLocal = new IPAddress(IPAddress.Parse("fe80::1").GetAddressBytes(), scopeid: 3);
        var remote = IPAddress.Parse("2001:db8::1");

        _ = Assert.Throws<NotSupportedException>(() => ZeroTierTcpRouteKeyV6.FromAddresses(linkLocal, localPort: 1, remote, remotePort: 2));
    }
}

