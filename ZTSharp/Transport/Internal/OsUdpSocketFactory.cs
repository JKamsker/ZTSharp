using System;
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
        return CreateSocketCore(
            localPort,
            enableIpv6,
            CreateUdp4Bound,
            CreateUdp6DualModeBound,
            CreateUdp6OnlyBound);
    }

    internal static UdpClient CreateSocketCore(
        int localPort,
        bool enableIpv6,
        Func<int, UdpClient> createUdp4Bound,
        Func<int, UdpClient> createUdp6DualModeBound,
        Func<int, UdpClient> createUdp6OnlyBound)
    {
        ArgumentNullException.ThrowIfNull(createUdp4Bound);
        ArgumentNullException.ThrowIfNull(createUdp6DualModeBound);
        ArgumentNullException.ThrowIfNull(createUdp6OnlyBound);

        if (!enableIpv6)
        {
            return createUdp4Bound(localPort);
        }

        try
        {
            return createUdp6DualModeBound(localPort);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
        }

        try
        {
            return createUdp6OnlyBound(localPort);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
        }

        return createUdp4Bound(localPort);
    }

    private static UdpClient CreateUdp4Bound(int localPort)
    {
        var udp4 = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp4.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
            return udp4;
        }
        catch
        {
            udp4.Dispose();
            throw;
        }
    }

    private static UdpClient CreateUdp6DualModeBound(int localPort)
    {
        var udp6 = new UdpClient(AddressFamily.InterNetworkV6);
        try
        {
            udp6.Client.DualMode = true;
            udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, localPort));
            return udp6;
        }
        catch
        {
            udp6.Dispose();
            throw;
        }
    }

    private static UdpClient CreateUdp6OnlyBound(int localPort)
    {
        var udp6 = new UdpClient(AddressFamily.InterNetworkV6);
        try
        {
            udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, localPort));
            return udp6;
        }
        catch
        {
            udp6.Dispose();
            throw;
        }
    }

    private static void TryDisableWindowsUdpConnReset(UdpClient udp, Action<string>? log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            udp.Client.IOControl((IOControlCode)WindowsSioUdpConnReset, CreateWindowsSioUdpConnResetInputBuffer(disableConnReset: true), null);
        }
        catch (SocketException ex)
        {
            log?.Invoke($"Failed to disable UDP connection reset handling (SocketException {ex.SocketErrorCode}: {ex.Message}).");
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

    internal static byte[] CreateWindowsSioUdpConnResetInputBuffer(bool disableConnReset)
        => BitConverter.GetBytes(disableConnReset ? 0 : 1);
}
