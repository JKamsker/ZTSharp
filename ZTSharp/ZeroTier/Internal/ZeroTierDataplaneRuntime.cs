using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneRuntime : IAsyncDisposable
{
    private const int IndexVerb = 27;

    private readonly ZeroTierUdpTransport _udp;
    private readonly NodeId _rootNodeId;
    private readonly IPEndPoint _rootEndpoint;
    private readonly byte[] _rootKey;
    private readonly byte _rootProtocolVersion;
    private readonly ZeroTierIdentity _localIdentity;
    private readonly ulong _networkId;
    private readonly byte[] _inlineCom;
    private readonly IPAddress? _localManagedIpV4;
    private readonly byte[]? _localManagedIpV4Bytes;
    private readonly IPAddress[] _localManagedIpsV6;
    private readonly ZeroTierMac _localMac;

    private readonly Channel<ZeroTierUdpDatagram> _peerQueue = Channel.CreateUnbounded<ZeroTierUdpDatagram>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _dispatcherLoop;
    private readonly Task _peerLoop;

    private readonly ZeroTierDataplaneRootClient _rootClient;
    private readonly ZeroTierDataplanePeerSecurity _peerSecurity;
    private readonly ConcurrentDictionary<IPAddress, NodeId> _managedIpToNodeId = new();
    private readonly ZeroTierDataplaneRouteRegistry _routes;
    private readonly ZeroTierDataplanePeerPacketHandler _peerPackets;
    private readonly ZeroTierDataplanePeerDatagramProcessor _peerDatagrams;

    private int _traceRxRemaining = 200;
    private bool _disposed;

    public ZeroTierDataplaneRuntime(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        byte rootProtocolVersion,
        ZeroTierIdentity localIdentity,
        ulong networkId,
        IPAddress? localManagedIpV4,
        IReadOnlyList<IPAddress> localManagedIpsV6,
        byte[] inlineCom)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(localManagedIpsV6);
        ArgumentNullException.ThrowIfNull(inlineCom);

        if (localManagedIpV4 is null && localManagedIpsV6.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localManagedIpV4), "At least one managed IP (IPv4 or IPv6) is required.");
        }

        if (localManagedIpV4 is not null && localManagedIpV4.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentOutOfRangeException(nameof(localManagedIpV4), "Managed IPv4 must be an IPv4 address.");
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
        _localManagedIpV4 = localManagedIpV4;
        _localManagedIpV4Bytes = localManagedIpV4?.GetAddressBytes();
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

        var icmpv6 = new ZeroTierDataplaneIcmpv6Handler(this, _localMac, _localManagedIpsV6);
        var ip = new ZeroTierDataplaneIpHandler(
            sender: this,
            routes: _routes,
            managedIpToNodeId: _managedIpToNodeId,
            icmpv6: icmpv6,
            networkId: _networkId,
            localMac: _localMac,
            localManagedIpV4: _localManagedIpV4,
            localManagedIpV4Bytes: _localManagedIpV4Bytes,
            localManagedIpsV6: _localManagedIpsV6);
        _peerPackets = new ZeroTierDataplanePeerPacketHandler(_networkId, _localMac, ip);
        _peerDatagrams = new ZeroTierDataplanePeerDatagramProcessor(localIdentity.NodeId, _peerSecurity, _peerPackets);

        _dispatcherLoop = Task.Run(DispatcherLoopAsync, CancellationToken.None);
        _peerLoop = Task.Run(PeerLoopAsync, CancellationToken.None);
    }

    public NodeId NodeId => _localIdentity.NodeId;

    public IPEndPoint LocalUdp => _udp.LocalEndpoint;

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
        AddressFamily addressFamily,
        ushort localPort,
        Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> onSyn)
        => _routes.TryRegisterTcpListener(addressFamily, localPort, onSyn);

    public void UnregisterTcpListener(AddressFamily addressFamily, ushort localPort)
        => _routes.UnregisterTcpListener(addressFamily, localPort);

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

        try
        {
            await _udp.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _dispatcherLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await _peerLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        _peerSecurity.Dispose();
    }

    private async Task DispatcherLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            ZeroTierUdpDatagram datagram;
            try
            {
                datagram = await _udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var packetBytes = datagram.Payload.ToArray();
            if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
            {
                continue;
            }

            if (decoded.Header.Destination != _localIdentity.NodeId)
            {
                continue;
            }

            if (ZeroTierTrace.Enabled && _traceRxRemaining > 0)
            {
                _traceRxRemaining--;
                ZeroTierTrace.WriteLine(
                    $"[zerotier] RX raw: src={decoded.Header.Source} dst={decoded.Header.Destination} cipher={decoded.Header.CipherSuite} flags=0x{decoded.Header.Flags:x2} verbRaw=0x{decoded.Header.VerbRaw:x2} via {datagram.RemoteEndPoint}.");
            }

            if (decoded.Header.Source == _rootNodeId)
            {
                if (!ZeroTierPacketCrypto.Dearmor(packetBytes, _rootKey))
                {
                    continue;
                }

                if ((packetBytes[IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
                {
                    if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
                    {
                        continue;
                    }

                    packetBytes = uncompressed;
                }

                var verb = (ZeroTierVerb)(packetBytes[IndexVerb] & 0x1F);
                var payload = packetBytes.AsSpan(ZeroTierPacketHeader.Length);

                _rootClient.TryDispatchResponse(verb, payload);
                continue;
            }

            if (!_peerQueue.Writer.TryWrite(datagram))
            {
                return;
            }
        }
    }

    private async Task PeerLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            ZeroTierUdpDatagram datagram;
            try
            {
                datagram = await _peerQueue.Reader.ReadAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }

            await _peerDatagrams.ProcessAsync(datagram, token).ConfigureAwait(false);
        }
    }

    private Task<byte[]> GetPeerKeyAsync(NodeId peerNodeId, CancellationToken cancellationToken)
        => _peerSecurity.GetPeerKeyAsync(peerNodeId, cancellationToken);

}
