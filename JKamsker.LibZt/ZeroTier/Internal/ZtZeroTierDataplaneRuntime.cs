using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Net;
using JKamsker.LibZt.ZeroTier.Protocol;
using JKamsker.LibZt.ZeroTier.Transport;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal sealed class ZtZeroTierDataplaneRuntime : IAsyncDisposable
{
    private const int IndexVerb = 27;

    private const int OkIndexInReVerb = ZtZeroTierPacketHeader.Length;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

    private const ushort EtherTypeArp = 0x0806;

    private readonly ZtZeroTierUdpTransport _udp;
    private readonly ZtNodeId _rootNodeId;
    private readonly IPEndPoint _rootEndpoint;
    private readonly byte[] _rootKey;
    private readonly ZtZeroTierIdentity _localIdentity;
    private readonly ulong _networkId;
    private readonly byte[] _inlineCom;
    private readonly IPAddress _localManagedIp;
    private readonly byte[] _localManagedIpV4;
    private readonly ZtZeroTierMac _localMac;

    private readonly Channel<ZtZeroTierUdpDatagram> _peerQueue = Channel.CreateUnbounded<ZtZeroTierUdpDatagram>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _dispatcherLoop;
    private readonly Task _peerLoop;

    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ZtZeroTierIdentity>> _pendingWhois = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<(uint TotalKnown, ZtNodeId[] Members)>> _pendingGather = new();

    private readonly ConcurrentDictionary<ZtNodeId, byte[]> _peerKeys = new();
    private readonly SemaphoreSlim _peerKeyLock = new(1, 1);

    private readonly ConcurrentDictionary<ZtZeroTierTcpRouteKey, ZtZeroTierRoutedIpv4Link> _routes = new();
    private readonly ConcurrentDictionary<ushort, Func<ZtNodeId, ReadOnlyMemory<byte>, CancellationToken, Task>> _tcpSynHandlers = new();

    private bool _disposed;

    public ZtZeroTierDataplaneRuntime(
        ZtZeroTierUdpTransport udp,
        ZtNodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        ZtZeroTierIdentity localIdentity,
        ulong networkId,
        IPAddress localManagedIp,
        byte[] inlineCom)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(localManagedIp);
        ArgumentNullException.ThrowIfNull(inlineCom);

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

        if (localManagedIp.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("Only IPv4 is supported in the TCP MVP.");
        }

        _udp = udp;
        _rootNodeId = rootNodeId;
        _rootEndpoint = rootEndpoint;
        _rootKey = rootKey;
        _localIdentity = localIdentity;
        _networkId = networkId;
        _inlineCom = inlineCom;
        _localManagedIp = localManagedIp;
        _localManagedIpV4 = localManagedIp.GetAddressBytes();
        _localMac = ZtZeroTierMac.FromAddress(localIdentity.NodeId, networkId);

        _dispatcherLoop = Task.Run(DispatcherLoopAsync, CancellationToken.None);
        _peerLoop = Task.Run(PeerLoopAsync, CancellationToken.None);
    }

    public ZtNodeId NodeId => _localIdentity.NodeId;

    public IPEndPoint LocalUdp => _udp.LocalEndpoint;

    public ZtZeroTierRoutedIpv4Link RegisterTcpRoute(ZtNodeId peerNodeId, IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var routeKey = ZtZeroTierTcpRouteKey.FromEndpoints(localEndpoint, remoteEndpoint);
        var link = new ZtZeroTierRoutedIpv4Link(this, routeKey, peerNodeId);
        if (!_routes.TryAdd(routeKey, link))
        {
            throw new InvalidOperationException($"TCP route already registered: {routeKey}.");
        }

        return link;
    }

    public void UnregisterRoute(ZtZeroTierTcpRouteKey routeKey)
        => _routes.TryRemove(routeKey, out _);

    public bool TryRegisterTcpListener(
        ushort localPort,
        Func<ZtNodeId, ReadOnlyMemory<byte>, CancellationToken, Task> onSyn)
        => _tcpSynHandlers.TryAdd(localPort, onSyn);

    public void UnregisterTcpListener(ushort localPort)
        => _tcpSynHandlers.TryRemove(localPort, out _);

    public async Task<ZtNodeId> ResolveNodeIdAsync(IPAddress managedIp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(managedIp);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var group = ZtZeroTierMulticastGroup.DeriveForAddressResolution(managedIp);
        var (totalKnown, members) = await MulticastGatherAsync(group, gatherLimit: 32, cancellationToken).ConfigureAwait(false);
        if (members.Length == 0)
        {
            throw new InvalidOperationException($"Could not resolve '{managedIp}' to a ZeroTier node id (no multicast-gather results).");
        }

        var remoteNodeId = members[0];
        if (ZtZeroTierTrace.Enabled)
        {
            var list = string.Join(", ", members.Take(8).Select(member => member.ToString()));
            var suffix = members.Length > 8 ? ", ..." : string.Empty;
            ZtZeroTierTrace.WriteLine($"[zerotier] Resolve {managedIp} -> {remoteNodeId} (members: {members.Length}/{totalKnown}: {list}{suffix}; root: {_rootNodeId} via {_rootEndpoint}).");
        }

        return remoteNodeId;
    }

    public async ValueTask SendIpv4Async(ZtNodeId peerNodeId, ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = await GetPeerKeyAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
        var remoteMac = ZtZeroTierMac.FromAddress(peerNodeId, _networkId);
        var packetId = GeneratePacketId();
        var packet = ZtZeroTierExtFramePacketBuilder.BuildPacket(
            packetId,
            destination: peerNodeId,
            source: _localIdentity.NodeId,
            networkId: _networkId,
            inlineCom: _inlineCom,
            to: remoteMac,
            from: _localMac,
            etherType: ZtZeroTierFrameCodec.EtherTypeIpv4,
            frame: ipv4Packet.Span,
            sharedKey: key);

        await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendEthernetFrameAsync(
        ZtNodeId peerNodeId,
        ushort etherType,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = await GetPeerKeyAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
        var remoteMac = ZtZeroTierMac.FromAddress(peerNodeId, _networkId);
        var packetId = GeneratePacketId();
        var packet = ZtZeroTierExtFramePacketBuilder.BuildPacket(
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
            ZtZeroTierUdpDatagram datagram;
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
            if (!ZtZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
            {
                continue;
            }

            if (decoded.Header.Destination != _localIdentity.NodeId)
            {
                continue;
            }

            if (decoded.Header.Source == _rootNodeId)
            {
                if (!ZtZeroTierPacketCrypto.Dearmor(packetBytes, _rootKey))
                {
                    continue;
                }

                if ((packetBytes[IndexVerb] & ZtZeroTierPacketHeader.VerbFlagCompressed) != 0)
                {
                    if (!ZtZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
                    {
                        continue;
                    }

                    packetBytes = uncompressed;
                }

                var verb = (ZtZeroTierVerb)(packetBytes[IndexVerb] & 0x1F);
                var payload = packetBytes.AsSpan(ZtZeroTierPacketHeader.Length);

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
            ZtZeroTierUdpDatagram datagram;
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

    private async Task ProcessPeerDatagramAsync(ZtZeroTierUdpDatagram datagram, CancellationToken cancellationToken)
    {
        var packetBytes = datagram.Payload.ToArray();
        if (!ZtZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
        {
            return;
        }

        if (decoded.Header.Destination != _localIdentity.NodeId)
        {
            return;
        }

        var peerNodeId = decoded.Header.Source;

        var key = await GetPeerKeyAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
        if (!ZtZeroTierPacketCrypto.Dearmor(packetBytes, key))
        {
            return;
        }

        if ((packetBytes[IndexVerb] & ZtZeroTierPacketHeader.VerbFlagCompressed) != 0)
        {
            if (!ZtZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
            {
                return;
            }

            packetBytes = uncompressed;
        }

        var verb = (ZtZeroTierVerb)(packetBytes[IndexVerb] & 0x1F);
        var payload = packetBytes.AsSpan(ZtZeroTierPacketHeader.Length);

        switch (verb)
        {
            case ZtZeroTierVerb.MulticastFrame:
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
                }

                return;
            }
            case ZtZeroTierVerb.Frame:
            {
                if (!ZtZeroTierFrameCodec.TryParseFramePayload(payload, out var networkId, out var etherType, out var frame))
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

                if (etherType == ZtZeroTierFrameCodec.EtherTypeIpv4)
                {
                    var ipv4Packet = packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
                    await HandleIpv4PacketAsync(peerNodeId, ipv4Packet, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            case ZtZeroTierVerb.ExtFrame:
            {
                if (!ZtZeroTierFrameCodec.TryParseExtFramePayload(
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

                if (to != _localMac || from != ZtZeroTierMac.FromAddress(peerNodeId, _networkId))
                {
                    return;
                }

                if (etherType == EtherTypeArp)
                {
                    await HandleArpFrameAsync(peerNodeId, frame, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (etherType == ZtZeroTierFrameCodec.EtherTypeIpv4)
                {
                    var ipv4Packet = packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
                    await HandleIpv4PacketAsync(peerNodeId, ipv4Packet, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            default:
                return;
        }
    }

    private async Task HandleIpv4PacketAsync(ZtNodeId peerNodeId, ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken)
    {
        if (!ZtIpv4Codec.TryParse(ipv4Packet.Span, out var src, out var dst, out var protocol, out var ipPayload))
        {
            return;
        }

        if (!dst.Equals(_localManagedIp) || protocol != ZtTcpCodec.ProtocolNumber)
        {
            return;
        }

        if (!ZtTcpCodec.TryParse(ipPayload, out var srcPort, out var dstPort, out _, out _, out var flags, out _, out _))
        {
            return;
        }

        var routeKey = new ZtZeroTierTcpRouteKey(
            LocalIp: BinaryPrimitives.ReadUInt32BigEndian(dst.GetAddressBytes()),
            LocalPort: dstPort,
            RemoteIp: BinaryPrimitives.ReadUInt32BigEndian(src.GetAddressBytes()),
            RemotePort: srcPort);

        if (_routes.TryGetValue(routeKey, out var route))
        {
            route.IncomingWriter.TryWrite(ipv4Packet);
            return;
        }

        if ((flags & ZtTcpCodec.Flags.Syn) != 0 &&
            (flags & ZtTcpCodec.Flags.Ack) == 0 &&
            _tcpSynHandlers.TryGetValue(dstPort, out var handler))
        {
            await handler(peerNodeId, ipv4Packet, cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask HandleArpFrameAsync(ZtNodeId peerNodeId, ReadOnlySpan<byte> frame, CancellationToken cancellationToken)
    {
        if (!TryParseArpRequest(frame, out var senderMac, out var senderIp, out var targetIp))
        {
            return ValueTask.CompletedTask;
        }

        if (!targetIp.SequenceEqual(_localManagedIpV4))
        {
            return ValueTask.CompletedTask;
        }

        var reply = BuildArpReply(senderMac, senderIp);
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

        if (htype != 1 || ptype != ZtZeroTierFrameCodec.EtherTypeIpv4 || hlen != 6 || plen != 4 || oper != 1)
        {
            return false;
        }

        senderMac = packet.Slice(8, 6);
        senderIp = packet.Slice(14, 4);
        targetIp = packet.Slice(24, 4);
        return true;
    }

    private byte[] BuildArpReply(ReadOnlySpan<byte> requesterMac, ReadOnlySpan<byte> requesterIp)
    {
        var reply = new byte[28];
        var span = reply.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(0, 2), 1); // HTYPE ethernet
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), ZtZeroTierFrameCodec.EtherTypeIpv4); // PTYPE IPv4
        span[4] = 6; // HLEN
        span[5] = 4; // PLEN
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(6, 2), 2); // OPER reply

        Span<byte> localMac = stackalloc byte[6];
        _localMac.CopyTo(localMac);

        localMac.CopyTo(span.Slice(8, 6)); // SHA
        _localManagedIpV4.CopyTo(span.Slice(14, 4)); // SPA
        requesterMac.CopyTo(span.Slice(18, 6)); // THA
        requesterIp.CopyTo(span.Slice(24, 4)); // TPA

        return reply;
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
            if (!ZtZeroTierCertificateOfMembershipCodec.TryGetSerializedLength(payload.Slice(ptr), out var comLen))
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

    private bool TryDispatchRootResponse(ZtZeroTierVerb verb, ReadOnlySpan<byte> payload)
    {
        switch (verb)
        {
            case ZtZeroTierVerb.Ok:
            {
                if (payload.Length < 1 + 8)
                {
                    return false;
                }

                var inReVerb = (ZtZeroTierVerb)(payload[0] & 0x1F);
                var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1, 8));

                if (inReVerb == ZtZeroTierVerb.Whois &&
                    _pendingWhois.TryRemove(inRePacketId, out var whoisTcs))
                {
                    if (payload.Length < (OkIndexPayload - OkIndexInReVerb))
                    {
                        whoisTcs.TrySetException(new InvalidOperationException("WHOIS OK payload too short."));
                        return true;
                    }

                    try
                    {
                        var identity = ZtZeroTierIdentityCodec.Deserialize(payload.Slice(1 + 8), out _);
                        whoisTcs.TrySetResult(identity);
                    }
                    catch (FormatException ex)
                    {
                        whoisTcs.TrySetException(ex);
                    }

                    return true;
                }

                if (inReVerb == ZtZeroTierVerb.MulticastGather &&
                    _pendingGather.TryRemove(inRePacketId, out var gatherTcs))
                {
                    if (!ZtZeroTierMulticastGatherCodec.TryParseOkPayload(
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
            case ZtZeroTierVerb.Error:
            {
                if (payload.Length < 1 + 8 + 1)
                {
                    return false;
                }

                var inReVerb = (ZtZeroTierVerb)(payload[0] & 0x1F);
                var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1, 8));
                var errorCode = payload[1 + 8];
                ulong? networkId = null;
                if (payload.Length >= 1 + 8 + 1 + 8)
                {
                    networkId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1 + 8 + 1, 8));
                }

                if (inReVerb == ZtZeroTierVerb.Whois &&
                    _pendingWhois.TryRemove(inRePacketId, out var whoisTcs))
                {
                    whoisTcs.TrySetException(new InvalidOperationException(FormatError(inReVerb, errorCode, networkId)));
                    return true;
                }

                if (inReVerb == ZtZeroTierVerb.MulticastGather &&
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

    private async Task<byte[]> GetPeerKeyAsync(ZtNodeId peerNodeId, CancellationToken cancellationToken)
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
            ZtZeroTierC25519.Agree(_localIdentity.PrivateKey!, identity.PublicKey, key);
            _peerKeys[peerNodeId] = key;
            return key;
        }
        finally
        {
            _peerKeyLock.Release();
        }
    }

    private async Task<ZtZeroTierIdentity> WhoisAsync(ZtNodeId targetNodeId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        var payload = new byte[5];
        WriteUInt40(payload, targetNodeId.Value);

        var packetId = GeneratePacketId();
        var header = new ZtZeroTierPacketHeader(
            PacketId: packetId,
            Destination: _rootNodeId,
            Source: _localIdentity.NodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZtZeroTierVerb.Whois);

        var packet = ZtZeroTierPacketCodec.Encode(header, payload);
        ZtZeroTierPacketCrypto.Armor(packet, _rootKey, encryptPayload: true);

        var tcs = new TaskCompletionSource<ZtZeroTierIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private async Task<(uint TotalKnown, ZtNodeId[] Members)> MulticastGatherAsync(
        ZtZeroTierMulticastGroup group,
        uint gatherLimit,
        CancellationToken cancellationToken)
    {
        var payload = ZtZeroTierMulticastGatherCodec.EncodeRequestPayload(_networkId, group, gatherLimit, _inlineCom);

        var packetId = GeneratePacketId();
        var header = new ZtZeroTierPacketHeader(
            PacketId: packetId,
            Destination: _rootNodeId,
            Source: _localIdentity.NodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZtZeroTierVerb.MulticastGather);

        var packet = ZtZeroTierPacketCodec.Encode(header, payload);
        ZtZeroTierPacketCrypto.Armor(packet, _rootKey, encryptPayload: true);

        var tcs = new TaskCompletionSource<(uint TotalKnown, ZtNodeId[] Members)>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private static string FormatError(ZtZeroTierVerb inReVerb, byte errorCode, ulong? networkId)
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
            ZtZeroTierVerb.MulticastGather => "ERROR(MULTICAST_GATHER)",
            ZtZeroTierVerb.Whois => "ERROR(WHOIS)",
            _ => $"ERROR({inReVerb})"
        };

        return networkId is null
            ? $"{prefix}: {message}"
            : $"{prefix}: {message} (network: 0x{networkId:x16})";
    }
}
