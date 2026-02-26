using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneRuntime : IAsyncDisposable
{
    private readonly ZeroTierUdpTransport _udp;
    private readonly NodeId _rootNodeId;
    private readonly IPEndPoint _rootEndpoint;
    private readonly byte[] _rootKey;
    private readonly byte _rootProtocolVersion;
    private readonly ZeroTierIdentity _localIdentity;
    private readonly ulong _networkId;
    private readonly byte[] _inlineCom;
    private readonly IPAddress[] _localManagedIpsV4;
    private readonly byte[][] _localManagedIpsV4Bytes;
    private readonly IPAddress[] _localManagedIpsV6;
    private readonly ZeroTierMac _localMac;
    private readonly ConcurrentDictionary<NodeId, ZeroTierDirectEndpointManager> _directEndpoints = new();

    private readonly Channel<ZeroTierUdpDatagram> _peerQueue = Channel.CreateBounded<ZeroTierUdpDatagram>(new BoundedChannelOptions(capacity: 2048)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    private long _peerQueueDropCount;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _dispatcherLoop;
    private readonly Task _peerLoop;

    private readonly ZeroTierDataplaneRootClient _rootClient;
    private readonly ZeroTierDataplanePeerSecurity _peerSecurity;
    private readonly ManagedIpToNodeIdCache _managedIpToNodeId = new();
    private readonly ZeroTierDataplaneRouteRegistry _routes;
    private readonly ZeroTierDataplanePeerPacketHandler _peerPackets;
    private readonly ZeroTierDataplanePeerDatagramProcessor _peerDatagrams;
    private readonly ZeroTierDataplaneRxLoops _rxLoops;

    private bool _disposed;

    public ZeroTierDataplaneRuntime(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        byte rootProtocolVersion,
        ZeroTierIdentity localIdentity,
        ulong networkId,
        IReadOnlyList<IPAddress> localManagedIpsV4,
        IReadOnlyList<IPAddress> localManagedIpsV6,
        byte[] inlineCom)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(localManagedIpsV6);
        ArgumentNullException.ThrowIfNull(inlineCom);

        if (localManagedIpsV4.Count == 0 && localManagedIpsV6.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localManagedIpsV4), "At least one managed IP (IPv4 or IPv6) is required.");
        }

        for (var i = 0; i < localManagedIpsV4.Count; i++)
        {
            if (localManagedIpsV4[i].AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentOutOfRangeException(nameof(localManagedIpsV4), "All IPv4 managed IPs must be IPv4.");
            }
        }

        for (var i = 0; i < localManagedIpsV6.Count; i++)
        {
            if (localManagedIpsV6[i].AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw new ArgumentOutOfRangeException(nameof(localManagedIpsV6), "All IPv6 managed IPs must be IPv6.");
            }
        }

        _udp = udp;
        _rootNodeId = rootNodeId;
        _rootEndpoint = rootEndpoint;
        _rootKey = rootKey;
        _rootProtocolVersion = rootProtocolVersion;
        _localIdentity = localIdentity;
        _networkId = networkId;
        _inlineCom = inlineCom;
        _localManagedIpsV4 = localManagedIpsV4.Count == 0 ? Array.Empty<IPAddress>() : localManagedIpsV4.ToArray();
        _localManagedIpsV4Bytes = _localManagedIpsV4.Length == 0
            ? Array.Empty<byte[]>()
            : _localManagedIpsV4.Select(ip => ip.GetAddressBytes()).ToArray();
        _localManagedIpsV6 = localManagedIpsV6.Count == 0 ? Array.Empty<IPAddress>() : localManagedIpsV6.ToArray();
        _localMac = ZeroTierMac.FromAddress(localIdentity.NodeId, networkId);

        _routes = new ZeroTierDataplaneRouteRegistry(this);
        _rootClient = new ZeroTierDataplaneRootClient(
            udp,
            rootNodeId,
            rootEndpoint,
            rootKey,
            rootProtocolVersion,
            localIdentity.NodeId,
            networkId,
            inlineCom);
        _peerSecurity = new ZeroTierDataplanePeerSecurity(udp, _rootClient, localIdentity);

        var icmpv6 = new ZeroTierDataplaneIcmpv6Handler(this, _localMac, _localManagedIpsV6, _managedIpToNodeId);
        var ip = new ZeroTierDataplaneIpHandler(
            sender: this,
            routes: _routes,
            managedIpToNodeId: _managedIpToNodeId,
            icmpv6: icmpv6,
            networkId: _networkId,
            localMac: _localMac,
            localManagedIpsV4: _localManagedIpsV4,
            localManagedIpsV4Bytes: _localManagedIpsV4Bytes,
            localManagedIpsV6: _localManagedIpsV6);
        _peerPackets = new ZeroTierDataplanePeerPacketHandler(_networkId, _localMac, ip, handleControlAsync: HandlePeerControlPacketAsync);
        _peerDatagrams = new ZeroTierDataplanePeerDatagramProcessor(localIdentity.NodeId, _peerSecurity, _peerPackets);
        _rxLoops = new ZeroTierDataplaneRxLoops(
            _udp,
            _rootNodeId,
            _rootEndpoint,
            _rootKey,
            _localIdentity.NodeId,
            _rootClient,
            _peerDatagrams,
            handleRootControlAsync: HandleRootControlPacketAsync,
            onPeerQueueDrop: () =>
            {
                Interlocked.Increment(ref _peerQueueDropCount);
                if (ZeroTierTrace.Enabled)
                {
                    ZeroTierTrace.WriteLine("[zerotier] Drop: peer queue is full.");
                }
            });

        _dispatcherLoop = Task.Run(() => _rxLoops.DispatcherLoopAsync(_peerQueue.Writer, _cts.Token), CancellationToken.None);
        _peerLoop = Task.Run(() => _rxLoops.PeerLoopAsync(_peerQueue.Reader, _cts.Token), CancellationToken.None);
    }

    public NodeId NodeId => _localIdentity.NodeId;

    public IPEndPoint LocalUdp => _udp.LocalEndpoint;

    public long PeerQueueDropCount => Interlocked.Read(ref _peerQueueDropCount);

    public IZeroTierRoutedIpLink RegisterTcpRoute(NodeId peerNodeId, IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _routes.RegisterTcpRoute(peerNodeId, localEndpoint, remoteEndpoint);
    }

    public void UnregisterRoute(ZeroTierTcpRouteKey routeKey)
        => _routes.UnregisterRoute(routeKey);

    public void UnregisterRoute(ZeroTierTcpRouteKeyV6 routeKey)
        => _routes.UnregisterRoute(routeKey);

    public bool TryRegisterTcpListener(
        IPAddress localAddress,
        ushort localPort,
        Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> onSyn)
        => _routes.TryRegisterTcpListener(localAddress, localPort, onSyn);

    public void UnregisterTcpListener(IPAddress localAddress, ushort localPort)
        => _routes.UnregisterTcpListener(localAddress, localPort);

    public bool TryRegisterUdpPort(AddressFamily addressFamily, ushort localPort, ChannelWriter<ZeroTierRoutedIpPacket> handler)
        => _routes.TryRegisterUdpPort(addressFamily, localPort, handler);

    public void UnregisterUdpPort(AddressFamily addressFamily, ushort localPort)
        => _routes.UnregisterUdpPort(addressFamily, localPort);

    public async Task<NodeId> ResolveNodeIdAsync(IPAddress managedIp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(managedIp);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _rootClient.ResolveNodeIdAsync(managedIp, _managedIpToNodeId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendIpv4Async(NodeId peerNodeId, ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = await GetPeerKeyAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
        var peerProtocolVersion = _peerSecurity.GetPeerProtocolVersionOrDefault(peerNodeId);
        var remoteMac = ZeroTierMac.FromAddress(peerNodeId, _networkId);
        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var packet = ZeroTierExtFramePacketBuilder.BuildPacket(
            packetId,
            destination: peerNodeId,
            source: _localIdentity.NodeId,
            networkId: _networkId,
            inlineCom: _inlineCom,
            to: remoteMac,
            from: _localMac,
            etherType: ZeroTierFrameCodec.EtherTypeIpv4,
            frame: ipv4Packet.Span,
            sharedKey: key,
            remoteProtocolVersion: peerProtocolVersion);

        await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendEthernetFrameAsync(
        NodeId peerNodeId,
        ushort etherType,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = await GetPeerKeyAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
        var peerProtocolVersion = _peerSecurity.GetPeerProtocolVersionOrDefault(peerNodeId);
        var remoteMac = ZeroTierMac.FromAddress(peerNodeId, _networkId);
        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var packet = ZeroTierExtFramePacketBuilder.BuildPacket(
            packetId,
            destination: peerNodeId,
            source: _localIdentity.NodeId,
            networkId: _networkId,
            inlineCom: _inlineCom,
            to: remoteMac,
            from: _localMac,
            etherType: etherType,
            frame: frame.Span,
            sharedKey: key,
            remoteProtocolVersion: peerProtocolVersion);

        await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _peerQueue.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);

        await _udp.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _dispatcherLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException) when (_cts.IsCancellationRequested)
        {
        }

        try
        {
            await _peerLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException) when (_cts.IsCancellationRequested)
        {
        }

        _cts.Dispose();
        _peerSecurity.Dispose();
    }

    private Task<byte[]> GetPeerKeyAsync(NodeId peerNodeId, CancellationToken cancellationToken)
        => _peerSecurity.GetPeerKeyAsync(peerNodeId, cancellationToken);

    private ValueTask HandleRootControlPacketAsync(
        ZeroTierVerb verb,
        ReadOnlyMemory<byte> payload,
        IPEndPoint receivedVia,
        CancellationToken cancellationToken)
    {
        if (verb != ZeroTierVerb.Rendezvous)
        {
            return ValueTask.CompletedTask;
        }

        if (!ZeroTierRendezvousCodec.TryParse(payload.Span, out var rendezvous) || rendezvous.With.Value == 0)
        {
            return ValueTask.CompletedTask;
        }

        var directEndpoints = GetOrCreateDirectEndpointManager(rendezvous.With);
        return directEndpoints.HandleRendezvousFromRootAsync(payload, receivedVia, cancellationToken);
    }

    private ValueTask HandlePeerControlPacketAsync(
        NodeId peerNodeId,
        ZeroTierVerb verb,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (verb != ZeroTierVerb.PushDirectPaths)
        {
            return ValueTask.CompletedTask;
        }

        var directEndpoints = GetOrCreateDirectEndpointManager(peerNodeId);
        return directEndpoints.HandlePushDirectPathsFromRemoteAsync(payload, cancellationToken);
    }

    private ZeroTierDirectEndpointManager GetOrCreateDirectEndpointManager(NodeId peerNodeId)
        => _directEndpoints.GetOrAdd(peerNodeId, id => new ZeroTierDirectEndpointManager(_udp, _rootEndpoint, id));

}
