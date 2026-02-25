using System.Net;
using System.Net.Sockets;

namespace ZTSharp.Transport.Internal;

internal static class OsUdpSocketFactory
{
    private const int WindowsSioUdpConnReset = unchecked((int)0x9800000C);

    public static UdpClient Create(int localPort, bool enableIpv6, Action<string>? log = null)
    {
        var udp = CreateSocketCore(localPort, enableIpv6);
        TryDisableWindowsUdpConnReset(udp, log);
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
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
        }

        var udpFallback = new UdpClient(AddressFamily.InterNetwork);
        udpFallback.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
        return udpFallback;
    }

    private static void TryDisableWindowsUdpConnReset(UdpClient udp, Action<string>? log)
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
            log?.Invoke("Failed to disable UDP connection reset handling (SocketException).");
        }
        catch (PlatformNotSupportedException)
        {
            log?.Invoke("Failed to disable UDP connection reset handling (PlatformNotSupportedException).");
        }
        catch (NotSupportedException)
        {
            log?.Invoke("Failed to disable UDP connection reset handling (NotSupportedException).");
        }
        catch (ObjectDisposedException)
        {
            log?.Invoke("Failed to disable UDP connection reset handling (ObjectDisposedException).");
        }
        catch (InvalidOperationException)
        {
            log?.Invoke("Failed to disable UDP connection reset handling (InvalidOperationException).");
        }
    }
}
