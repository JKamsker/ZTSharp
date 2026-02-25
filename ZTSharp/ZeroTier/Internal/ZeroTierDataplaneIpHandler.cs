using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneIpHandler
{
    private const ushort EtherTypeArp = 0x0806;

    private readonly ZeroTierDataplaneRuntime _sender;
    private readonly ZeroTierDataplaneRouteRegistry _routes;
    private readonly ConcurrentDictionary<IPAddress, NodeId> _managedIpToNodeId;
    private readonly ZeroTierDataplaneIcmpv6Handler _icmpv6;

    private readonly ulong _networkId;
    private readonly ZeroTierMac _localMac;
    private readonly IPAddress? _localManagedIpV4;
    private readonly byte[]? _localManagedIpV4Bytes;
    private readonly IPAddress[] _localManagedIpsV6;

    public ZeroTierDataplaneIpHandler(
        ZeroTierDataplaneRuntime sender,
        ZeroTierDataplaneRouteRegistry routes,
        ConcurrentDictionary<IPAddress, NodeId> managedIpToNodeId,
        ZeroTierDataplaneIcmpv6Handler icmpv6,
        ulong networkId,
        ZeroTierMac localMac,
        IPAddress? localManagedIpV4,
        byte[]? localManagedIpV4Bytes,
        IPAddress[] localManagedIpsV6)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(managedIpToNodeId);
        ArgumentNullException.ThrowIfNull(icmpv6);
        ArgumentNullException.ThrowIfNull(localManagedIpsV6);

        _sender = sender;
        _routes = routes;
        _managedIpToNodeId = managedIpToNodeId;
        _icmpv6 = icmpv6;

        _networkId = networkId;
        _localMac = localMac;
        _localManagedIpV4 = localManagedIpV4;
        _localManagedIpV4Bytes = localManagedIpV4Bytes;
        _localManagedIpsV6 = localManagedIpsV6;
    }

    public async ValueTask HandleIpv6PacketAsync(NodeId peerNodeId, ReadOnlyMemory<byte> ipv6Packet, CancellationToken cancellationToken)
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
            await _icmpv6.HandleAsync(peerNodeId, src, dst, hopLimit, icmpMessage, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (nextHeader == UdpCodec.ProtocolNumber && isUnicastToUs)
        {
            if (!UdpCodec.TryParse(ipPayload, out _, out var dstPort, out _))
            {
                return;
            }

            if (_routes.TryGetUdpHandler(AddressFamily.InterNetworkV6, dstPort, out var handler))
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
            if (_routes.TryGetRoute(routeKey, out var route))
            {
                route.IncomingWriter.TryWrite(ipv6Packet);
                return;
            }

            if ((flags & TcpCodec.Flags.Syn) != 0 &&
                (flags & TcpCodec.Flags.Ack) == 0 &&
                _routes.TryGetTcpSynHandler(AddressFamily.InterNetworkV6, dstPort, out var handler))
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

    public async ValueTask HandleIpv4PacketAsync(NodeId peerNodeId, ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken)
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

            if (_routes.TryGetUdpHandler(AddressFamily.InterNetwork, udpDstPort, out var udpHandler))
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

        if (_routes.TryGetRoute(routeKey, out var route))
        {
            route.IncomingWriter.TryWrite(ipv4Packet);
            return;
        }

        if ((flags & TcpCodec.Flags.Syn) != 0 &&
            (flags & TcpCodec.Flags.Ack) == 0 &&
            _routes.TryGetTcpSynHandler(AddressFamily.InterNetwork, dstPort, out var handler))
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

    public ValueTask HandleArpFrameAsync(NodeId peerNodeId, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        var localManagedIpV4Bytes = _localManagedIpV4Bytes;
        if (localManagedIpV4Bytes is null)
        {
            return ValueTask.CompletedTask;
        }

        if (!ZeroTierArp.TryParseRequest(frame.Span, out var senderMac, out var senderIp, out var targetIp))
        {
            return ValueTask.CompletedTask;
        }

        if (!targetIp.SequenceEqual(localManagedIpV4Bytes))
        {
            return ValueTask.CompletedTask;
        }

        var reply = ZeroTierArp.BuildReply(_localMac, localManagedIpV4Bytes, senderMac, senderIp);
        return _sender.SendEthernetFrameAsync(peerNodeId, EtherTypeArp, reply, cancellationToken);
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

            await _sender.SendIpv4Async(peerNodeId, ip, cancellationToken).ConfigureAwait(false);
        }
        else if (localIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var ip = Ipv6Codec.Encode(
                source: localIp,
                destination: remoteIp,
                nextHeader: TcpCodec.ProtocolNumber,
                payload: tcp,
                hopLimit: 64);

            await _sender.SendEthernetFrameAsync(peerNodeId, ZeroTierFrameCodec.EtherTypeIpv6, ip, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ushort GenerateIpIdentification()
    {
        Span<byte> buffer = stackalloc byte[2];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
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
}

