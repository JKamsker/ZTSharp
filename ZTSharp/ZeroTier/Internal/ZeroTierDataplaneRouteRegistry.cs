using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneRouteRegistry
{
    private readonly ZeroTierDataplaneRuntime _runtime;

    private readonly ConcurrentDictionary<ZeroTierTcpRouteKey, ZeroTierRoutedIpv4Link> _routesV4 = new();
    private readonly ConcurrentDictionary<ZeroTierTcpRouteKeyV6, ZeroTierRoutedIpv6Link> _routesV6 = new();
    private readonly ConcurrentDictionary<ushort, TcpListenerPortRegistrationsV4> _tcpListenersV4 = new();
    private readonly ConcurrentDictionary<ushort, TcpListenerPortRegistrationsV6> _tcpListenersV6 = new();
    private readonly ConcurrentDictionary<ushort, ChannelWriter<ZeroTierRoutedIpPacket>> _udpHandlersV4 = new();
    private readonly ConcurrentDictionary<ushort, ChannelWriter<ZeroTierRoutedIpPacket>> _udpHandlersV6 = new();

    private sealed class TcpListenerPortRegistrationsV4
    {
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<uint, Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task>> _specific = new();
        private Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task>? _wildcard;

        public bool TryAdd(IPAddress localAddress, Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> handler)
        {
            var isWildcard = localAddress.Equals(IPAddress.Any);
            var key = isWildcard ? 0u : BinaryPrimitives.ReadUInt32BigEndian(localAddress.GetAddressBytes());

            lock (_gate)
            {
                if (isWildcard)
                {
                    if (_wildcard is not null || !_specific.IsEmpty)
                    {
                        return false;
                    }

                    _wildcard = handler;
                    return true;
                }

                if (_wildcard is not null)
                {
                    return false;
                }

                return _specific.TryAdd(key, handler);
            }
        }

        public bool TryRemove(IPAddress localAddress)
        {
            var isWildcard = localAddress.Equals(IPAddress.Any);
            var key = isWildcard ? 0u : BinaryPrimitives.ReadUInt32BigEndian(localAddress.GetAddressBytes());

            lock (_gate)
            {
                if (isWildcard)
                {
                    if (_wildcard is null)
                    {
                        return false;
                    }

                    _wildcard = null;
                    return true;
                }

                return _specific.TryRemove(key, out _);
            }
        }

        public bool TryGet(IPAddress destinationIp, out Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> handler)
        {
            if (!_specific.IsEmpty)
            {
                var key = BinaryPrimitives.ReadUInt32BigEndian(destinationIp.GetAddressBytes());
                if (_specific.TryGetValue(key, out var found))
                {
                    handler = found;
                    return true;
                }
            }

            var wildcard = Volatile.Read(ref _wildcard);
            if (wildcard is not null)
            {
                handler = wildcard;
                return true;
            }

            handler = default!;
            return false;
        }

        public bool IsEmpty => _specific.IsEmpty && Volatile.Read(ref _wildcard) is null;
    }

    private readonly record struct Ipv6Key(ulong High, ulong Low)
    {
        public static Ipv6Key FromManagedIp(IPAddress address)
        {
            var canonical = ZeroTierIpAddressCanonicalization.CanonicalizeForManagedIpComparison(address);
            var bytes = canonical.GetAddressBytes();
            return new Ipv6Key(
                High: BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(0, 8)),
                Low: BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8, 8)));
        }
    }

    private sealed class TcpListenerPortRegistrationsV6
    {
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<Ipv6Key, Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task>> _specific = new();
        private Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task>? _wildcard;

        public bool TryAdd(IPAddress localAddress, Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> handler)
        {
            var isWildcard = localAddress.Equals(IPAddress.IPv6Any);
            var key = isWildcard ? default : Ipv6Key.FromManagedIp(localAddress);

            lock (_gate)
            {
                if (isWildcard)
                {
                    if (_wildcard is not null || !_specific.IsEmpty)
                    {
                        return false;
                    }

                    _wildcard = handler;
                    return true;
                }

                if (_wildcard is not null)
                {
                    return false;
                }

                return _specific.TryAdd(key, handler);
            }
        }

        public bool TryRemove(IPAddress localAddress)
        {
            var isWildcard = localAddress.Equals(IPAddress.IPv6Any);
            var key = isWildcard ? default : Ipv6Key.FromManagedIp(localAddress);

            lock (_gate)
            {
                if (isWildcard)
                {
                    if (_wildcard is null)
                    {
                        return false;
                    }

                    _wildcard = null;
                    return true;
                }

                return _specific.TryRemove(key, out _);
            }
        }

        public bool TryGet(IPAddress destinationIp, out Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> handler)
        {
            if (!_specific.IsEmpty)
            {
                var key = Ipv6Key.FromManagedIp(destinationIp);
                if (_specific.TryGetValue(key, out var found))
                {
                    handler = found;
                    return true;
                }
            }

            var wildcard = Volatile.Read(ref _wildcard);
            if (wildcard is not null)
            {
                handler = wildcard;
                return true;
            }

            handler = default!;
            return false;
        }

        public bool IsEmpty => _specific.IsEmpty && Volatile.Read(ref _wildcard) is null;
    }

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
        IPAddress localAddress,
        ushort localPort,
        Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> onSyn)
    {
        ArgumentNullException.ThrowIfNull(localAddress);

        return localAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => _tcpListenersV4.GetOrAdd(localPort, static _ => new TcpListenerPortRegistrationsV4()).TryAdd(localAddress, onSyn),
            AddressFamily.InterNetworkV6 => _tcpListenersV6.GetOrAdd(localPort, static _ => new TcpListenerPortRegistrationsV6()).TryAdd(localAddress, onSyn),
            _ => throw new ArgumentOutOfRangeException(nameof(localAddress), localAddress.AddressFamily, "Unsupported address family.")
        };
    }

    public void UnregisterTcpListener(IPAddress localAddress, ushort localPort)
    {
        ArgumentNullException.ThrowIfNull(localAddress);

        if (localAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            if (_tcpListenersV4.TryGetValue(localPort, out var registrations))
            {
                registrations.TryRemove(localAddress);
                if (registrations.IsEmpty)
                {
                    _tcpListenersV4.TryRemove(localPort, out _);
                }
            }

            return;
        }

        if (localAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (_tcpListenersV6.TryGetValue(localPort, out var registrations))
            {
                registrations.TryRemove(localAddress);
                if (registrations.IsEmpty)
                {
                    _tcpListenersV6.TryRemove(localPort, out _);
                }
            }

            return;
        }

        throw new ArgumentOutOfRangeException(nameof(localAddress), localAddress.AddressFamily, "Unsupported address family.");
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

    public bool TryGetTcpSynHandler(
        AddressFamily addressFamily,
        IPAddress destinationIp,
        ushort localPort,
        out Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> handler)
    {
        if (addressFamily == AddressFamily.InterNetwork)
        {
            if (_tcpListenersV4.TryGetValue(localPort, out var registrations) && registrations.TryGet(destinationIp, out var existing))
            {
                handler = existing;
                return true;
            }

            handler = default!;
            return false;
        }

        if (addressFamily == AddressFamily.InterNetworkV6)
        {
            if (_tcpListenersV6.TryGetValue(localPort, out var registrations) && registrations.TryGet(destinationIp, out var existing))
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
