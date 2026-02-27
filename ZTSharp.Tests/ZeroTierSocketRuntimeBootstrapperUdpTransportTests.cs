using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class ZeroTierSocketRuntimeBootstrapperUdpTransportTests
{
    [Fact]
    public async Task CreateUdpTransport_Default_ReturnsSingleSocket()
    {
        var transport = ZeroTierSocketRuntimeBootstrapper.CreateUdpTransport(new ZeroTierMultipathOptions(), enableIpv6: false);
        try
        {
            Assert.Single(transport.LocalSockets);
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateUdpTransport_MultipathEnabled_UsesMultipleSockets()
    {
        var transport = ZeroTierSocketRuntimeBootstrapper.CreateUdpTransport(
            new ZeroTierMultipathOptions { Enabled = true, UdpSocketCount = 3 },
            enableIpv6: false);

        try
        {
            Assert.Equal(3, transport.LocalSockets.Count);
            Assert.Equal(new[] { 0, 1, 2 }, transport.LocalSockets.Select(s => s.Id).ToArray());
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }
}
