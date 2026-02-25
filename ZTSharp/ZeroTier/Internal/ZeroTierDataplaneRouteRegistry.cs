using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneRouteRegistry
{
    private readonly ZeroTierDataplaneRuntime _runtime;

    private readonly ConcurrentDictionary<ZeroTierTcpRouteKey, ZeroTierRoutedIpv4Link> _routesV4 = new();
    private readonly ConcurrentDictionary<ZeroTierTcpRouteKeyV6, ZeroTierRoutedIpv6Link> _routesV6 = new();
    private readonly ConcurrentDictionary<ushort, Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task>> _tcpSynHandlersV4 = new();
    private readonly ConcurrentDictionary<ushort, Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task>> _tcpSynHandlersV6 = new();
    private readonly ConcurrentDictionary<ushort, ChannelWriter<ZeroTierRoutedIpPacket>> _udpHandlersV4 = new();
    private readonly ConcurrentDictionary<ushort, ChannelWriter<ZeroTierRoutedIpPacket>> _udpHandlersV6 = new();

    public ZeroTierDataplaneRouteRegistry(ZeroTierDataplaneRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public IZeroTierRoutedIpLink RegisterTcpRoute(NodeId peerNodeId, IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        ArgumentNullException.ThrowIfNull(remoteEndpoint);

        if (localEndpoint.Address.AddressFamily != remoteEndpoint.Address.AddressFamily)
        {
            throw new NotSupportedException("Local and remote address families must match.");
        }

        if (localEndpoint.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            var routeKey = ZeroTierTcpRouteKey.FromEndpoints(localEndpoint, remoteEndpoint);
            var link = new ZeroTierRoutedIpv4Link(_runtime, routeKey, peerNodeId);
            if (!_routesV4.TryAdd(routeKey, link))
            {
                throw new InvalidOperationException($"TCP route already registered: {routeKey}.");
            }

            return link;
        }

        if (localEndpoint.Address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var routeKey = ZeroTierTcpRouteKeyV6.FromEndpoints(localEndpoint, remoteEndpoint);
            var link = new ZeroTierRoutedIpv6Link(_runtime, routeKey, peerNodeId);
            if (!_routesV6.TryAdd(routeKey, link))
            {
                throw new InvalidOperationException($"TCP route already registered: {routeKey}.");
            }

            return link;
        }

        throw new NotSupportedException($"Unsupported address family: {localEndpoint.Address.AddressFamily}.");
    }

    public void UnregisterRoute(ZeroTierTcpRouteKey routeKey)
        => _routesV4.TryRemove(routeKey, out _);

    public void UnregisterRoute(ZeroTierTcpRouteKeyV6 routeKey)
        => _routesV6.TryRemove(routeKey, out _);

    public bool TryRegisterTcpListener(
        AddressFamily addressFamily,
        ushort localPort,
        Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> onSyn)
        => addressFamily switch
        {
            AddressFamily.InterNetwork => _tcpSynHandlersV4.TryAdd(localPort, onSyn),
            AddressFamily.InterNetworkV6 => _tcpSynHandlersV6.TryAdd(localPort, onSyn),
            _ => throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, "Unsupported address family.")
        };

    public void UnregisterTcpListener(AddressFamily addressFamily, ushort localPort)
    {
        if (addressFamily == AddressFamily.InterNetwork)
        {
            _tcpSynHandlersV4.TryRemove(localPort, out _);
            return;
        }

        if (addressFamily == AddressFamily.InterNetworkV6)
        {
            _tcpSynHandlersV6.TryRemove(localPort, out _);
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, "Unsupported address family.");
    }

    public bool TryRegisterUdpPort(AddressFamily addressFamily, ushort localPort, ChannelWriter<ZeroTierRoutedIpPacket> handler)
        => addressFamily switch
        {
            AddressFamily.InterNetwork => _udpHandlersV4.TryAdd(localPort, handler),
            AddressFamily.InterNetworkV6 => _udpHandlersV6.TryAdd(localPort, handler),
            _ => throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, "Unsupported address family.")
        };

    public void UnregisterUdpPort(AddressFamily addressFamily, ushort localPort)
    {
        if (addressFamily == AddressFamily.InterNetwork)
        {
            _udpHandlersV4.TryRemove(localPort, out _);
            return;
        }

        if (addressFamily == AddressFamily.InterNetworkV6)
        {
            _udpHandlersV6.TryRemove(localPort, out _);
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, "Unsupported address family.");
    }

    public bool TryGetRoute(ZeroTierTcpRouteKey routeKey, out ZeroTierRoutedIpv4Link route)
        => _routesV4.TryGetValue(routeKey, out route!);

    public bool TryGetRoute(ZeroTierTcpRouteKeyV6 routeKey, out ZeroTierRoutedIpv6Link route)
        => _routesV6.TryGetValue(routeKey, out route!);

    public bool TryGetTcpSynHandler(AddressFamily addressFamily, ushort localPort, out Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> handler)
    {
        if (addressFamily == AddressFamily.InterNetwork)
        {
            if (_tcpSynHandlersV4.TryGetValue(localPort, out var existing))
            {
                handler = existing;
                return true;
            }

            handler = default!;
            return false;
        }

        if (addressFamily == AddressFamily.InterNetworkV6)
        {
            if (_tcpSynHandlersV6.TryGetValue(localPort, out var existing))
            {
                handler = existing;
                return true;
            }

            handler = default!;
            return false;
        }

        throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, "Unsupported address family.");
    }

    public bool TryGetUdpHandler(AddressFamily addressFamily, ushort localPort, out ChannelWriter<ZeroTierRoutedIpPacket> handler)
    {
        if (addressFamily == AddressFamily.InterNetwork)
        {
            if (_udpHandlersV4.TryGetValue(localPort, out var existing))
            {
                handler = existing;
                return true;
            }

            handler = default!;
            return false;
        }

        if (addressFamily == AddressFamily.InterNetworkV6)
        {
            if (_udpHandlersV6.TryGetValue(localPort, out var existing))
            {
                handler = existing;
                return true;
            }

            handler = default!;
            return false;
        }

        throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, "Unsupported address family.");
    }
}
