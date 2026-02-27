using System.Net;
using System.Net.Sockets;
using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Transport;

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

    [Fact]
    public void CreateUdpTransport_MultipathEnabled_RequiresAtLeastOneSocket()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ZeroTierSocketRuntimeBootstrapper.CreateUdpTransport(
            new ZeroTierMultipathOptions { Enabled = true, UdpSocketCount = 0 },
            enableIpv6: false));
    }

    [Fact]
    public async Task CreateUdpTransport_SingleSocket_HonorsLocalUdpPorts()
    {
        const int attempts = 25;
        for (var i = 0; i < attempts; i++)
        {
            var port = GetAvailableUdpPort();
            var transport = default(IZeroTierUdpTransport);
            try
            {
                transport = ZeroTierSocketRuntimeBootstrapper.CreateUdpTransport(
                    new ZeroTierMultipathOptions { Enabled = true, UdpSocketCount = 1, LocalUdpPorts = new[] { port } },
                    enableIpv6: false);

                Assert.Single(transport.LocalSockets);
                Assert.Equal(port, transport.LocalSockets[0].LocalEndpoint.Port);
                return;
            }
            catch (SocketException)
            {
            }
            finally
            {
                if (transport is not null)
                {
                    await transport.DisposeAsync();
                }
            }
        }

        Assert.Fail($"Failed to bind a UDP socket to an available port after {attempts} attempts.");
    }

    private static int GetAvailableUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }
}
