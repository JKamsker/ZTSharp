using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneRuntime : IAsyncDisposable
{
    private const int IndexVerb = 27;

    private const int OkIndexInReVerb = ZeroTierPacketHeader.Length;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

    private const ushort EtherTypeArp = 0x0806;
    private const int HelloPayloadMinLength = 13 + (5 + 1 + ZeroTierIdentity.PublicKeyLength + 1);

    private readonly ZeroTierUdpTransport _udp;
    private readonly NodeId _rootNodeId;
    private readonly IPEndPoint _rootEndpoint;
    private readonly byte[] _rootKey;
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

    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ZeroTierIdentity>> _pendingWhois = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<(uint TotalKnown, NodeId[] Members)>> _pendingGather = new();

    private readonly ConcurrentDictionary<NodeId, byte[]> _peerKeys = new();
    private readonly SemaphoreSlim _peerKeyLock = new(1, 1);

    private readonly ConcurrentDictionary<IPAddress, NodeId> _managedIpToNodeId = new();

    private readonly ConcurrentDictionary<ZeroTierTcpRouteKey, ZeroTierRoutedIpv4Link> _routesV4 = new();
    private readonly ConcurrentDictionary<ZeroTierTcpRouteKeyV6, ZeroTierRoutedIpv6Link> _routesV6 = new();
    private readonly ConcurrentDictionary<ushort, Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task>> _tcpSynHandlersV4 = new();
    private readonly ConcurrentDictionary<ushort, Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task>> _tcpSynHandlersV6 = new();
    private readonly ConcurrentDictionary<ushort, ChannelWriter<ZeroTierRoutedIpPacket>> _udpHandlersV4 = new();
    private readonly ConcurrentDictionary<ushort, ChannelWriter<ZeroTierRoutedIpPacket>> _udpHandlersV6 = new();

    private int _traceRxRemaining = 200;

    private bool _disposed;

    public ZeroTierDataplaneRuntime(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
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

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

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
        _localIdentity = localIdentity;
        _networkId = networkId;
        _inlineCom = inlineCom;
        _localManagedIpV4 = localManagedIpV4;
        _localManagedIpV4Bytes = localManagedIpV4?.GetAddressBytes();
        _localManagedIpsV6 = localManagedIpsV6.Count == 0 ? Array.Empty<IPAddress>() : localManagedIpsV6.ToArray();
        _localMac = ZeroTierMac.FromAddress(localIdentity.NodeId, networkId);

        _dispatcherLoop = Task.Run(DispatcherLoopAsync, CancellationToken.None);
        _peerLoop = Task.Run(PeerLoopAsync, CancellationToken.None);
    }

    public NodeId NodeId => _localIdentity.NodeId;

    public IPEndPoint LocalUdp => _udp.LocalEndpoint;

    public IZeroTierRoutedIpLink RegisterTcpRoute(NodeId peerNodeId, IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArgumentNullException.ThrowIfNull(localEndpoint);
        ArgumentNullException.ThrowIfNull(remoteEndpoint);

        if (localEndpoint.Address.AddressFamily != remoteEndpoint.Address.AddressFamily)
        {
            throw new NotSupportedException("Local and remote address families must match.");
        }

        if (localEndpoint.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            var routeKey = ZeroTierTcpRouteKey.FromEndpoints(localEndpoint, remoteEndpoint);
            var link = new ZeroTierRoutedIpv4Link(this, routeKey, peerNodeId);
            if (!_routesV4.TryAdd(routeKey, link))
            {
                throw new InvalidOperationException($"TCP route already registered: {routeKey}.");
            }

            return link;
        }

        if (localEndpoint.Address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var routeKey = ZeroTierTcpRouteKeyV6.FromEndpoints(localEndpoint, remoteEndpoint);
            var link = new ZeroTierRoutedIpv6Link(this, routeKey, peerNodeId);
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

    public async Task<NodeId> ResolveNodeIdAsync(IPAddress managedIp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(managedIp);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_managedIpToNodeId.TryGetValue(managedIp, out var cachedNodeId))
        {
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine($"[zerotier] Resolve {managedIp} -> {cachedNodeId} (cache).");
            }

            return cachedNodeId;
        }

        var group = ZeroTierMulticastGroup.DeriveForAddressResolution(managedIp);
        var (totalKnown, members) = await MulticastGatherAsync(group, gatherLimit: 32, cancellationToken).ConfigureAwait(false);
        if (members.Length == 0)
        {
            throw new InvalidOperationException($"Could not resolve '{managedIp}' to a ZeroTier node id (no multicast-gather results).");
        }

        var remoteNodeId = members[0];
        if (ZeroTierTrace.Enabled)
        {
            var list = string.Join(", ", members.Take(8).Select(member => member.ToString()));
            var suffix = members.Length > 8 ? ", ..." : string.Empty;
            ZeroTierTrace.WriteLine($"[zerotier] Resolve {managedIp} -> {remoteNodeId} (members: {members.Length}/{totalKnown}: {list}{suffix}; root: {_rootNodeId} via {_rootEndpoint}).");
        }

        return remoteNodeId;
    }

    public async ValueTask SendIpv4Async(NodeId peerNodeId, ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = await GetPeerKeyAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
        var remoteMac = ZeroTierMac.FromAddress(peerNodeId, _networkId);
        var packetId = GeneratePacketId();
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
            sharedKey: key);

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
        var remoteMac = ZeroTierMac.FromAddress(peerNodeId, _networkId);
        var packetId = GeneratePacketId();
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
            sharedKey: key);

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
        _peerKeyLock.Dispose();
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

                if (TryDispatchRootResponse(verb, payload))
                {
                    continue;
                }

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

            await ProcessPeerDatagramAsync(datagram, token).ConfigureAwait(false);
        }
    }

    private async Task ProcessPeerDatagramAsync(ZeroTierUdpDatagram datagram, CancellationToken cancellationToken)
    {
        var packetBytes = datagram.Payload.ToArray();
        if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
        {
            return;
        }

        if (decoded.Header.Destination != _localIdentity.NodeId)
        {
            return;
        }

        var peerNodeId = decoded.Header.Source;

        if (decoded.Header.CipherSuite == 0 && decoded.Header.Verb == ZeroTierVerb.Hello)
        {
            await HandleHelloAsync(peerNodeId, decoded.Header.PacketId, packetBytes, datagram.RemoteEndPoint, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var key = await GetPeerKeyAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
        if (!ZeroTierPacketCrypto.Dearmor(packetBytes, key))
        {
            return;
        }

        if ((packetBytes[IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
        {
            if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
            {
                return;
            }

            packetBytes = uncompressed;
        }

        var verb = (ZeroTierVerb)(packetBytes[IndexVerb] & 0x1F);
        var payload = packetBytes.AsSpan(ZeroTierPacketHeader.Length);

        switch (verb)
        {
            case ZeroTierVerb.MulticastFrame:
                {
                    if (!TryParseMulticastFramePayload(payload, out var networkId, out var etherType, out var frame))
                    {
                        return;
                    }

                    if (networkId != _networkId)
                    {
                        return;
                    }

                    if (etherType == EtherTypeArp)
                    {
                        await HandleArpFrameAsync(peerNodeId, frame, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (etherType == ZeroTierFrameCodec.EtherTypeIpv6)
                    {
                        var ipv6Packet = packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
                        await HandleIpv6PacketAsync(peerNodeId, ipv6Packet, cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }
            case ZeroTierVerb.Frame:
                {
                    if (!ZeroTierFrameCodec.TryParseFramePayload(payload, out var networkId, out var etherType, out var frame))
                    {
                        return;
                    }

                    if (networkId != _networkId)
                    {
                        return;
                    }

                    if (etherType == EtherTypeArp)
                    {
                        await HandleArpFrameAsync(peerNodeId, frame, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (etherType == ZeroTierFrameCodec.EtherTypeIpv4)
                    {
                        var ipv4Packet = packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
                        await HandleIpv4PacketAsync(peerNodeId, ipv4Packet, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (etherType == ZeroTierFrameCodec.EtherTypeIpv6)
                    {
                        var ipv6Packet = packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
                        await HandleIpv6PacketAsync(peerNodeId, ipv6Packet, cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }
            case ZeroTierVerb.ExtFrame:
                {
                    if (!ZeroTierFrameCodec.TryParseExtFramePayload(
                            payload,
                            out var networkId,
                            out _,
                            out _,
                            out var to,
                            out var from,
                            out var etherType,
                            out var frame))
                    {
                        return;
                    }

                    if (networkId != _networkId)
                    {
                        return;
                    }

                    if (to != _localMac || from != ZeroTierMac.FromAddress(peerNodeId, _networkId))
                    {
                        return;
                    }

                    if (etherType == EtherTypeArp)
                    {
                        await HandleArpFrameAsync(peerNodeId, frame, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (etherType == ZeroTierFrameCodec.EtherTypeIpv4)
                    {
                        var ipv4Packet = packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
                        await HandleIpv4PacketAsync(peerNodeId, ipv4Packet, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (etherType == ZeroTierFrameCodec.EtherTypeIpv6)
                    {
                        var ipv6Packet = packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
                        await HandleIpv6PacketAsync(peerNodeId, ipv6Packet, cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }
            default:
                return;
        }
    }

    private async ValueTask HandleHelloAsync(
        NodeId peerNodeId,
        ulong helloPacketId,
        byte[] packetBytes,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (packetBytes.Length < ZeroTierPacketHeader.Length + HelloPayloadMinLength)
        {
            return;
        }

        var payload = packetBytes.AsSpan(ZeroTierPacketHeader.Length);
        if (payload.Length < HelloPayloadMinLength)
        {
            return;
        }

        var helloTimestamp = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(5, 8));

        ZeroTierIdentity identity;
        try
        {
            identity = ZeroTierIdentityCodec.Deserialize(payload.Slice(13), out _);
        }
        catch (FormatException)
        {
            return;
        }

        if (identity.NodeId != peerNodeId || !identity.LocallyValidate())
        {
            return;
        }

        var sharedKey = new byte[48];
        ZeroTierC25519.Agree(_localIdentity.PrivateKey!, identity.PublicKey, sharedKey);

        if (!ZeroTierPacketCrypto.Dearmor(packetBytes, sharedKey))
        {
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine($"[zerotier] Drop: failed to authenticate HELLO from {peerNodeId} via {remoteEndPoint}.");
            }

            return;
        }

        _peerKeys[peerNodeId] = sharedKey;

        var okPacket = ZeroTierHelloOkPacketBuilder.BuildPacket(
            packetId: GeneratePacketId(),
            destination: peerNodeId,
            source: _localIdentity.NodeId,
            inRePacketId: helloPacketId,
            helloTimestampEcho: helloTimestamp,
            externalSurfaceAddress: remoteEndPoint,
            sharedKey: sharedKey);

        try
        {
            await _udp.SendAsync(remoteEndPoint, okPacket, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async ValueTask HandleIpv6PacketAsync(NodeId peerNodeId, ReadOnlyMemory<byte> ipv6Packet, CancellationToken cancellationToken)
    {
        if (_localManagedIpsV6.Length == 0)
        {
            return;
        }

        if (!Ipv6Codec.TryParse(ipv6Packet.Span, out var src, out var dst, out var nextHeader, out var hopLimit, out var ipPayload))
        {
            return;
        }

        var isUnicastToUs = TryGetLocalManagedIpv6(dst, out _);
        var isMulticast = dst.IsIPv6Multicast;

        if (!isUnicastToUs && !isMulticast)
        {
            return;
        }

        if (!IsUnspecifiedIpv6(src))
        {
            _managedIpToNodeId[src] = peerNodeId;
        }

        if (nextHeader == Icmpv6Codec.ProtocolNumber)
        {
            var icmpMessage = ipv6Packet.Slice(Ipv6Codec.HeaderLength, ipPayload.Length);
            await HandleIcmpv6Async(peerNodeId, src, dst, hopLimit, icmpMessage, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (nextHeader == UdpCodec.ProtocolNumber && isUnicastToUs)
        {
            if (!UdpCodec.TryParse(ipPayload, out _, out var dstPort, out _))
            {
                return;
            }

            if (_udpHandlersV6.TryGetValue(dstPort, out var handler))
            {
                handler.TryWrite(new ZeroTierRoutedIpPacket(peerNodeId, ipv6Packet));
            }

            return;
        }

        if (nextHeader == TcpCodec.ProtocolNumber && isUnicastToUs)
        {
            if (!TcpCodec.TryParse(ipPayload, out var srcPort, out var dstPort, out var seq, out _, out var flags, out _, out _))
            {
                return;
            }

            var routeKey = ZeroTierTcpRouteKeyV6.FromAddresses(dst, dstPort, src, srcPort);
            if (_routesV6.TryGetValue(routeKey, out var route))
            {
                route.IncomingWriter.TryWrite(ipv6Packet);
                return;
            }

            if ((flags & TcpCodec.Flags.Syn) != 0 &&
                (flags & TcpCodec.Flags.Ack) == 0 &&
                _tcpSynHandlersV6.TryGetValue(dstPort, out var handler))
            {
                await handler(peerNodeId, ipv6Packet, cancellationToken).ConfigureAwait(false);
                return;
            }

            if ((flags & TcpCodec.Flags.Syn) != 0 &&
                (flags & TcpCodec.Flags.Ack) == 0)
            {
                await SendTcpRstAsync(
                        peerNodeId,
                        localIp: dst,
                        remoteIp: src,
                        localPort: dstPort,
                        remotePort: srcPort,
                        acknowledgmentNumber: unchecked(seq + 1),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task HandleIpv4PacketAsync(NodeId peerNodeId, ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken)
    {
        if (!Ipv4Codec.TryParse(ipv4Packet.Span, out var src, out var dst, out var protocol, out var ipPayload))
        {
            return;
        }

        if (!dst.Equals(_localManagedIpV4))
        {
            return;
        }

        _managedIpToNodeId[src] = peerNodeId;

        if (protocol == UdpCodec.ProtocolNumber)
        {
            if (!UdpCodec.TryParse(ipPayload, out _, out var udpDstPort, out _))
            {
                return;
            }

            if (_udpHandlersV4.TryGetValue(udpDstPort, out var udpHandler))
            {
                udpHandler.TryWrite(new ZeroTierRoutedIpPacket(peerNodeId, ipv4Packet));
            }

            return;
        }

        if (protocol != TcpCodec.ProtocolNumber)
        {
            return;
        }

        if (!TcpCodec.TryParse(ipPayload, out var srcPort, out var dstPort, out var seq, out _, out var flags, out _, out _))
        {
            return;
        }

        var routeKey = new ZeroTierTcpRouteKey(
            LocalIp: BinaryPrimitives.ReadUInt32BigEndian(dst.GetAddressBytes()),
            LocalPort: dstPort,
            RemoteIp: BinaryPrimitives.ReadUInt32BigEndian(src.GetAddressBytes()),
            RemotePort: srcPort);

        if (_routesV4.TryGetValue(routeKey, out var route))
        {
            route.IncomingWriter.TryWrite(ipv4Packet);
            return;
        }

        if ((flags & TcpCodec.Flags.Syn) != 0 &&
            (flags & TcpCodec.Flags.Ack) == 0 &&
            _tcpSynHandlersV4.TryGetValue(dstPort, out var handler))
        {
            await handler(peerNodeId, ipv4Packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        if ((flags & TcpCodec.Flags.Syn) != 0 &&
            (flags & TcpCodec.Flags.Ack) == 0)
        {
            await SendTcpRstAsync(
                    peerNodeId,
                    localIp: dst,
                    remoteIp: src,
                    localPort: dstPort,
                    remotePort: srcPort,
                    acknowledgmentNumber: unchecked(seq + 1),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask HandleIcmpv6Async(
        NodeId peerNodeId,
        IPAddress sourceIp,
        IPAddress destinationIp,
        byte hopLimit,
        ReadOnlyMemory<byte> icmpMessage,
        CancellationToken cancellationToken)
    {
        var icmpSpan = icmpMessage.Span;

        if (!Icmpv6Codec.TryParse(icmpSpan, out var type, out var code, out _))
        {
            return;
        }

        // Echo request / reply
        if (type == 128 && code == 0)
        {
            if (IsUnspecifiedIpv6(sourceIp) || !TryGetLocalManagedIpv6(destinationIp, out _))
            {
                return;
            }

            var reply = icmpSpan.ToArray();
            reply[0] = 129; // Echo Reply
            reply[1] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2, 2), 0);

            var checksum = Icmpv6Codec.ComputeChecksum(destinationIp, sourceIp, reply);
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2, 2), checksum);

            var packet = Ipv6Codec.Encode(
                source: destinationIp,
                destination: sourceIp,
                nextHeader: Icmpv6Codec.ProtocolNumber,
                payload: reply,
                hopLimit: 64);

            await SendEthernetFrameAsync(peerNodeId, ZeroTierFrameCodec.EtherTypeIpv6, packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Neighbor Solicitation
        if (type == 135 && code == 0)
        {
            if (hopLimit != 255)
            {
                return;
            }

            if (icmpSpan.Length < 24)
            {
                return;
            }

            var targetIp = new IPAddress(icmpSpan.Slice(8, 16));
            if (IsUnspecifiedIpv6(sourceIp) || !TryGetLocalManagedIpv6(targetIp, out var localTarget))
            {
                return;
            }

            var na = new byte[32];
            var span = na.AsSpan();

            span[0] = 136; // Neighbor Advertisement
            span[1] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), 0); // checksum placeholder
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(4, 4), 0x6000_0000u); // solicited + override
            localTarget.GetAddressBytes().CopyTo(span.Slice(8, 16));

            span[24] = 2; // option type: Target Link-Layer Address
            span[25] = 1; // option length: 8 bytes
            _localMac.CopyTo(span.Slice(26, 6));

            var checksum = Icmpv6Codec.ComputeChecksum(localTarget, sourceIp, na);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), checksum);

            var packet = Ipv6Codec.Encode(
                source: localTarget,
                destination: sourceIp,
                nextHeader: Icmpv6Codec.ProtocolNumber,
                payload: na,
                hopLimit: 255);

            await SendEthernetFrameAsync(peerNodeId, ZeroTierFrameCodec.EtherTypeIpv6, packet, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryGetLocalManagedIpv6(IPAddress address, out IPAddress localIp)
    {
        for (var i = 0; i < _localManagedIpsV6.Length; i++)
        {
            var ip = _localManagedIpsV6[i];
            if (address.Equals(ip))
            {
                localIp = ip;
                return true;
            }
        }

        localIp = IPAddress.IPv6None;
        return false;
    }

    private static bool IsUnspecifiedIpv6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 16)
        {
            return false;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private ValueTask HandleArpFrameAsync(NodeId peerNodeId, ReadOnlySpan<byte> frame, CancellationToken cancellationToken)
    {
        var localManagedIpV4Bytes = _localManagedIpV4Bytes;
        if (localManagedIpV4Bytes is null)
        {
            return ValueTask.CompletedTask;
        }

        if (!TryParseArpRequest(frame, out var senderMac, out var senderIp, out var targetIp))
        {
            return ValueTask.CompletedTask;
        }

        if (!targetIp.SequenceEqual(localManagedIpV4Bytes))
        {
            return ValueTask.CompletedTask;
        }

        var reply = BuildArpReply(senderMac, senderIp, localManagedIpV4Bytes);
        return SendEthernetFrameAsync(peerNodeId, EtherTypeArp, reply, cancellationToken);
    }

    private static bool TryParseArpRequest(
        ReadOnlySpan<byte> packet,
        out ReadOnlySpan<byte> senderMac,
        out ReadOnlySpan<byte> senderIp,
        out ReadOnlySpan<byte> targetIp)
    {
        senderMac = default;
        senderIp = default;
        targetIp = default;

        if (packet.Length < 28)
        {
            return false;
        }

        var htype = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(0, 2));
        var ptype = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        var hlen = packet[4];
        var plen = packet[5];
        var oper = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));

        if (htype != 1 || ptype != ZeroTierFrameCodec.EtherTypeIpv4 || hlen != 6 || plen != 4 || oper != 1)
        {
            return false;
        }

        senderMac = packet.Slice(8, 6);
        senderIp = packet.Slice(14, 4);
        targetIp = packet.Slice(24, 4);
        return true;
    }

    private byte[] BuildArpReply(
        ReadOnlySpan<byte> requesterMac,
        ReadOnlySpan<byte> requesterIp,
        ReadOnlySpan<byte> localManagedIpV4Bytes)
    {
        if (localManagedIpV4Bytes.Length != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(localManagedIpV4Bytes), "Local IPv4 address bytes must be exactly 4 bytes.");
        }

        var reply = new byte[28];
        var span = reply.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(0, 2), 1); // HTYPE ethernet
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), ZeroTierFrameCodec.EtherTypeIpv4); // PTYPE IPv4
        span[4] = 6; // HLEN
        span[5] = 4; // PLEN
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(6, 2), 2); // OPER reply

        Span<byte> localMac = stackalloc byte[6];
        _localMac.CopyTo(localMac);

        localMac.CopyTo(span.Slice(8, 6)); // SHA
        localManagedIpV4Bytes.CopyTo(span.Slice(14, 4)); // SPA
        requesterMac.CopyTo(span.Slice(18, 6)); // THA
        requesterIp.CopyTo(span.Slice(24, 4)); // TPA

        return reply;
    }

    private async ValueTask SendTcpRstAsync(
        NodeId peerNodeId,
        IPAddress localIp,
        IPAddress remoteIp,
        ushort localPort,
        ushort remotePort,
        uint acknowledgmentNumber,
        CancellationToken cancellationToken)
    {
        if (localIp.AddressFamily != remoteIp.AddressFamily)
        {
            return;
        }

        var tcp = TcpCodec.Encode(
            sourceIp: localIp,
            destinationIp: remoteIp,
            sourcePort: localPort,
            destinationPort: remotePort,
            sequenceNumber: 0,
            acknowledgmentNumber: acknowledgmentNumber,
            flags: TcpCodec.Flags.Rst | TcpCodec.Flags.Ack,
            windowSize: 0,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        if (localIp.AddressFamily == AddressFamily.InterNetwork)
        {
            var ip = Ipv4Codec.Encode(
                source: localIp,
                destination: remoteIp,
                protocol: TcpCodec.ProtocolNumber,
                payload: tcp,
                identification: GenerateIpIdentification());

            await SendIpv4Async(peerNodeId, ip, cancellationToken).ConfigureAwait(false);
        }
        else if (localIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var ip = Ipv6Codec.Encode(
                source: localIp,
                destination: remoteIp,
                nextHeader: TcpCodec.ProtocolNumber,
                payload: tcp,
                hopLimit: 64);

            await SendEthernetFrameAsync(peerNodeId, ZeroTierFrameCodec.EtherTypeIpv6, ip, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ushort GenerateIpIdentification()
    {
        Span<byte> buffer = stackalloc byte[2];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    private static bool TryParseMulticastFramePayload(
        ReadOnlySpan<byte> payload,
        out ulong networkId,
        out ushort etherType,
        out ReadOnlySpan<byte> frame)
    {
        networkId = 0;
        etherType = 0;
        frame = default;

        if (payload.Length < 8 + 1 + 6 + 4 + 2)
        {
            return false;
        }

        networkId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(0, 8));
        var flags = payload[8];

        var ptr = 9;

        if ((flags & 0x01) != 0)
        {
            if (!ZeroTierCertificateOfMembershipCodec.TryGetSerializedLength(payload.Slice(ptr), out var comLen))
            {
                return false;
            }

            ptr += comLen;
        }

        if ((flags & 0x02) != 0)
        {
            if (payload.Length < ptr + 4)
            {
                return false;
            }

            ptr += 4;
        }

        if ((flags & 0x04) != 0)
        {
            if (payload.Length < ptr + 6)
            {
                return false;
            }

            ptr += 6;
        }

        if (payload.Length < ptr + 6 + 4 + 2)
        {
            return false;
        }

        etherType = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(ptr + 6 + 4, 2));
        frame = payload.Slice(ptr + 6 + 4 + 2);
        return true;
    }

    private bool TryDispatchRootResponse(ZeroTierVerb verb, ReadOnlySpan<byte> payload)
    {
        switch (verb)
        {
            case ZeroTierVerb.Ok:
                {
                    if (payload.Length < 1 + 8)
                    {
                        return false;
                    }

                    var inReVerb = (ZeroTierVerb)(payload[0] & 0x1F);
                    var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1, 8));

                    if (inReVerb == ZeroTierVerb.Whois &&
                        _pendingWhois.TryRemove(inRePacketId, out var whoisTcs))
                    {
                        if (payload.Length < (OkIndexPayload - OkIndexInReVerb))
                        {
                            whoisTcs.TrySetException(new InvalidOperationException("WHOIS OK payload too short."));
                            return true;
                        }

                        try
                        {
                            var identity = ZeroTierIdentityCodec.Deserialize(payload.Slice(1 + 8), out _);
                            whoisTcs.TrySetResult(identity);
                        }
                        catch (FormatException ex)
                        {
                            whoisTcs.TrySetException(ex);
                        }

                        return true;
                    }

                    if (inReVerb == ZeroTierVerb.MulticastGather &&
                        _pendingGather.TryRemove(inRePacketId, out var gatherTcs))
                    {
                        if (!ZeroTierMulticastGatherCodec.TryParseOkPayload(
                                payload.Slice(1 + 8),
                                out var okNetworkId,
                                out _,
                                out var totalKnown,
                                out var members) ||
                            okNetworkId != _networkId)
                        {
                            gatherTcs.TrySetException(new InvalidOperationException("Invalid MULTICAST_GATHER OK payload."));
                            return true;
                        }

                        gatherTcs.TrySetResult((totalKnown, members));
                        return true;
                    }

                    return false;
                }
            case ZeroTierVerb.Error:
                {
                    if (payload.Length < 1 + 8 + 1)
                    {
                        return false;
                    }

                    var inReVerb = (ZeroTierVerb)(payload[0] & 0x1F);
                    var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1, 8));
                    var errorCode = payload[1 + 8];
                    ulong? networkId = null;
                    if (payload.Length >= 1 + 8 + 1 + 8)
                    {
                        networkId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1 + 8 + 1, 8));
                    }

                    if (inReVerb == ZeroTierVerb.Whois &&
                        _pendingWhois.TryRemove(inRePacketId, out var whoisTcs))
                    {
                        whoisTcs.TrySetException(new InvalidOperationException(FormatError(inReVerb, errorCode, networkId)));
                        return true;
                    }

                    if (inReVerb == ZeroTierVerb.MulticastGather &&
                        _pendingGather.TryRemove(inRePacketId, out var gatherTcs))
                    {
                        gatherTcs.TrySetException(new InvalidOperationException(FormatError(inReVerb, errorCode, networkId)));
                        return true;
                    }

                    return false;
                }
            default:
                return false;
        }
    }

    private async Task<byte[]> GetPeerKeyAsync(NodeId peerNodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_peerKeys.TryGetValue(peerNodeId, out var existing))
        {
            return existing;
        }

        await _peerKeyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_peerKeys.TryGetValue(peerNodeId, out existing))
            {
                return existing;
            }

            var identity = await WhoisAsync(peerNodeId, timeout: TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var key = new byte[48];
            ZeroTierC25519.Agree(_localIdentity.PrivateKey!, identity.PublicKey, key);
            _peerKeys[peerNodeId] = key;
            return key;
        }
        finally
        {
            _peerKeyLock.Release();
        }
    }

    private async Task<ZeroTierIdentity> WhoisAsync(NodeId targetNodeId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        var payload = new byte[5];
        WriteUInt40(payload, targetNodeId.Value);

        var packetId = GeneratePacketId();
        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: _rootNodeId,
            Source: _localIdentity.NodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Whois);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, _rootKey, encryptPayload: true);

        var tcs = new TaskCompletionSource<ZeroTierIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingWhois.TryAdd(packetId, tcs))
        {
            throw new InvalidOperationException("Packet id collision while sending WHOIS.");
        }

        try
        {
            await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for WHOIS response after {timeout}.");
            }
        }
        finally
        {
            _pendingWhois.TryRemove(packetId, out _);
        }
    }

    private async Task<(uint TotalKnown, NodeId[] Members)> MulticastGatherAsync(
        ZeroTierMulticastGroup group,
        uint gatherLimit,
        CancellationToken cancellationToken)
    {
        var payload = ZeroTierMulticastGatherCodec.EncodeRequestPayload(_networkId, group, gatherLimit, _inlineCom);

        var packetId = GeneratePacketId();
        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: _rootNodeId,
            Source: _localIdentity.NodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.MulticastGather);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, _rootKey, encryptPayload: true);

        var tcs = new TaskCompletionSource<(uint TotalKnown, NodeId[] Members)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingGather.TryAdd(packetId, tcs))
        {
            throw new InvalidOperationException("Packet id collision while sending MULTICAST_GATHER.");
        }

        try
        {
            await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for MULTICAST_GATHER response after {TimeSpan.FromSeconds(5)}.");
            }
        }
        finally
        {
            _pendingGather.TryRemove(packetId, out _);
        }
    }

    private static ulong GeneratePacketId()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    private static void WriteUInt40(Span<byte> destination, ulong value)
    {
        if (destination.Length < 5)
        {
            throw new ArgumentException("Destination must be at least 5 bytes.", nameof(destination));
        }

        destination[0] = (byte)((value >> 32) & 0xFF);
        destination[1] = (byte)((value >> 24) & 0xFF);
        destination[2] = (byte)((value >> 16) & 0xFF);
        destination[3] = (byte)((value >> 8) & 0xFF);
        destination[4] = (byte)(value & 0xFF);
    }

    private static string FormatError(ZeroTierVerb inReVerb, byte errorCode, ulong? networkId)
    {
        var message = errorCode switch
        {
            0x01 => "Invalid request.",
            0x02 => "Bad/unsupported protocol version.",
            0x03 => "Object not found.",
            0x04 => "Identity collision.",
            0x05 => "Unsupported operation.",
            0x06 => "Network membership certificate required (COM update needed).",
            0x07 => "Network access denied (not authorized).",
            0x08 => "Unwanted multicast.",
            0x09 => "Network authentication required (external/2FA).",
            _ => $"Unknown error (0x{errorCode:x2})."
        };

        var prefix = inReVerb switch
        {
            ZeroTierVerb.MulticastGather => "ERROR(MULTICAST_GATHER)",
            ZeroTierVerb.Whois => "ERROR(WHOIS)",
            _ => $"ERROR({inReVerb})"
        };

        return networkId is null
            ? $"{prefix}: {message}"
            : $"{prefix}: {message} (network: 0x{networkId:x16})";
    }
}
