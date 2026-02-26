using System.Net.Sockets;
using ZTSharp.Transport.Internal;

namespace ZTSharp.Tests;

public sealed class OsUdpSocketFactoryTests
{
    [Fact]
    public void WindowsSioUdpConnResetInput_IsDword()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var buffer = OsUdpSocketFactory.CreateWindowsSioUdpConnResetInputBuffer(disableConnReset: true);
        Assert.Equal(4, buffer.Length);
        Assert.Equal(0, BitConverter.ToInt32(buffer, 0));
    }

    [Fact]
    public void CreateSocketCore_WhenDualModeFails_TriesIpv6OnlyBeforeIpv4()
    {
        var calls = new List<string>();

        UdpClient CreateUdp4(int _) { calls.Add("v4"); return new UdpClient(AddressFamily.InterNetwork); }
        UdpClient CreateDualMode(int _) { calls.Add("dual"); throw new SocketException((int)SocketError.AddressFamilyNotSupported); }
        UdpClient CreateIpv6Only(int _) { calls.Add("v6only"); return new UdpClient(AddressFamily.InterNetworkV6); }

        var socket = OsUdpSocketFactory.CreateSocketCore(
            localPort: 0,
            enableIpv6: true,
            createUdp4Bound: CreateUdp4,
            createUdp6DualModeBound: CreateDualMode,
            createUdp6OnlyBound: CreateIpv6Only);

        try
        {
            Assert.Equal(new[] { "dual", "v6only" }, calls);
            Assert.Equal(AddressFamily.InterNetworkV6, socket.Client.AddressFamily);
        }
        finally
        {
            socket.Dispose();
        }
    }
}

