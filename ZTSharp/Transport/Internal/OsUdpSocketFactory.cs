using System.Net;
using System.Net.Sockets;

namespace ZTSharp.Transport.Internal;

internal static class OsUdpSocketFactory
{
    private const int WindowsSioUdpConnReset = unchecked((int)0x9800000C);

    public static UdpClient Create(int localPort, bool enableIpv6)
    {
        var udp = CreateSocketCore(localPort, enableIpv6);
        TryDisableWindowsUdpConnReset(udp);
        return udp;
    }

    private static UdpClient CreateSocketCore(int localPort, bool enableIpv6)
    {
        if (!enableIpv6)
        {
            var udp4 = new UdpClient(AddressFamily.InterNetwork);
            udp4.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
            return udp4;
        }

        try
        {
            var udp6 = new UdpClient(AddressFamily.InterNetworkV6);
            udp6.Client.DualMode = true;
            udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, localPort));
            return udp6;
        }
        catch (SocketException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (NotSupportedException)
        {
        }

        var udpFallback = new UdpClient(AddressFamily.InterNetwork);
        udpFallback.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
        return udpFallback;
    }

    private static void TryDisableWindowsUdpConnReset(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            udp.Client.IOControl((IOControlCode)WindowsSioUdpConnReset, [0], null);
        }
        catch (SocketException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}

