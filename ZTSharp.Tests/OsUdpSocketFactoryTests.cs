using System.Net.Sockets;
using System.Reflection;
using ZTSharp.Transport.Internal;

namespace ZTSharp.Tests;

public sealed class OsUdpSocketFactoryTests
{
    [Fact]
    public void WindowsSioUdpConnResetInput_IsDword()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new Xunit.Sdk.SkipException("Windows-only IOControl buffer test.");
        }

        var buffer = OsUdpSocketFactory.CreateWindowsSioUdpConnResetInputBuffer(disableConnReset: true);
        Assert.Equal(4, buffer.Length);
        Assert.Equal(0, BitConverter.ToInt32(buffer, 0));
    }

    [Fact]
    public void CreateSocketCore_WhenDualModeFails_TriesIpv4BeforeIpv6Only()
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
            Assert.Equal(new[] { "dual", "v4" }, calls);
            Assert.Equal(AddressFamily.InterNetwork, socket.Client.AddressFamily);
        }
        finally
        {
            socket.Dispose();
        }
    }

    [Fact]
    public void CreateUdp6OnlyBound_SetsDualModeFalse()
    {
        if (!Socket.OSSupportsIPv6)
        {
            throw new Xunit.Sdk.SkipException("IPv6 not supported on this platform.");
        }

        var method = typeof(OsUdpSocketFactory).GetMethod("CreateUdp6OnlyBound", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        UdpClient? udp;
        try
        {
            udp = (UdpClient?)method!.Invoke(null, new object[] { 0 });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
            throw new Xunit.Sdk.SkipException($"IPv6 appears supported, but binding an IPv6 UDP socket failed: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
        Assert.NotNull(udp);

        try
        {
            Assert.False(udp!.Client.DualMode);
        }
        finally
        {
            udp!.Dispose();
        }
    }
}

