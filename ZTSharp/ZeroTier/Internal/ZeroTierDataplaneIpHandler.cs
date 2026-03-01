using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneIpHandler
{
    private readonly ZeroTierDataplaneRuntime _sender;
    private readonly ZeroTierDataplaneRouteRegistry _routes;
    private readonly ManagedIpToNodeIdCache _managedIpToNodeId;
    private readonly ZeroTierDataplaneIcmpv6Handler _icmpv6;
    private readonly ZeroTierTcpRstSender _tcpRst;

    private readonly ulong _networkId;
    private readonly ZeroTierMac _localMac;
    private readonly IPAddress[] _localManagedIpsV4;
    private readonly byte[][] _localManagedIpsV4Bytes;
    private readonly IPAddress[] _localManagedIpsV6;

    public ZeroTierDataplaneIpHandler(
        ZeroTierDataplaneRuntime sender,
        ZeroTierDataplaneRouteRegistry routes,
        ManagedIpToNodeIdCache managedIpToNodeId,
        ZeroTierDataplaneIcmpv6Handler icmpv6,
        ulong networkId,
        ZeroTierMac localMac,
        IPAddress[] localManagedIpsV4,
        byte[][] localManagedIpsV4Bytes,
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
        _tcpRst = new ZeroTierTcpRstSender(sender);

        _networkId = networkId;
        _localMac = localMac;
        _localManagedIpsV4 = localManagedIpsV4;
        _localManagedIpsV4Bytes = localManagedIpsV4Bytes;
        _localManagedIpsV6 = localManagedIpsV6;
    }

    public async ValueTask HandleIpv6PacketAsync(NodeId peerNodeId, ReadOnlyMemory<byte> ipv6Packet, CancellationToken cancellationToken)
    {
        if (_localManagedIpsV6.Length == 0)
        {
            return;
        }

        if (!Ipv6Codec.TryParseTransportPayload(
                ipv6Packet.Span,
                out var src,
                out var dst,
                out var protocol,
                out var hopLimit,
                out var ipPayload,
                out var transportPayloadOffset))
        {
            return;
        }

        if (protocol == 59)
        {
            return;
        }

        var isUnicastToUs = TryGetLocalManagedIpv6(dst, out _);
        var isMulticast = dst.IsIPv6Multicast;

        if (!isUnicastToUs && !isMulticast)
        {
            return;
        }

        if (protocol == Icmpv6Codec.ProtocolNumber)
        {
            var icmpMessage = ipv6Packet.Slice(transportPayloadOffset, ipPayload.Length);
            await _icmpv6.HandleAsync(peerNodeId, src, dst, hopLimit, icmpMessage, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (protocol == UdpCodec.ProtocolNumber && isUnicastToUs)
        {
            if (!UdpCodec.TryParseWithChecksum(src, dst, ipPayload, out _, out var dstPort, out _))
            {
                return;
            }

            if (_routes.TryGetUdpHandler(AddressFamily.InterNetworkV6, dstPort, out var handler))
            {
                handler.TryWrite(new ZeroTierRoutedIpPacket(peerNodeId, ipv6Packet));
            }

            return;
        }

        if (protocol == TcpCodec.ProtocolNumber && isUnicastToUs)
        {
            if (!TcpCodec.TryParseWithChecksum(src, dst, ipPayload, out var srcPort, out var dstPort, out var seq, out _, out var flags, out _, out var tcpPayload))
            {
                return;
            }

            var routeKey = ZeroTierTcpRouteKeyV6.FromAddresses(dst, dstPort, src, srcPort);
            if (_routes.TryGetRoute(routeKey, out var route))
            {
                if (!route.TryEnqueueIncoming(ipv6Packet))
                {
                    if (ZeroTierTrace.Enabled)
                    {
                        ZeroTierTrace.WriteLine($"[zerotier] Drop+RST: TCP route backlog overflow for {dst}:{dstPort} <- {src}:{srcPort} (drops={route.IncomingDropCount}).");
                    }

                    var ackIncrement = 0u;
                    if ((flags & TcpCodec.Flags.Syn) != 0)
                    {
                        ackIncrement++;
                    }

                    if ((flags & TcpCodec.Flags.Fin) != 0)
                    {
                        ackIncrement++;
                    }

                    try
                    {
                        await SendTcpRstAsync(
                                peerNodeId,
                                localIp: dst,
                                remoteIp: src,
                                localPort: dstPort,
                                remotePort: srcPort,
                                acknowledgmentNumber: unchecked(seq + (uint)tcpPayload.Length + ackIncrement),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or IOException)
                    {
                    }
                }

                return;
            }

            if ((flags & TcpCodec.Flags.Syn) != 0 &&
                (flags & TcpCodec.Flags.Ack) == 0 &&
                _routes.TryGetTcpSynHandler(AddressFamily.InterNetworkV6, destinationIp: dst, dstPort, out var handler))
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
        if (Ipv4Codec.IsFragmented(ipv4Packet.Span))
        {
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine("[zerotier] Drop: IPv4 fragments are not supported.");
            }

            return;
        }

        if (!Ipv4Codec.TryParse(ipv4Packet.Span, out var src, out var dst, out var protocol, out var ipPayload))
        {
            return;
        }

        if (!TryGetLocalManagedIpv4(dst, out dst))
        {
            return;
        }

        if (protocol == UdpCodec.ProtocolNumber)
        {
            if (!UdpCodec.TryParseWithChecksum(src, dst, ipPayload, out _, out var udpDstPort, out _))
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

        if (!TcpCodec.TryParseWithChecksum(src, dst, ipPayload, out var srcPort, out var dstPort, out var seq, out _, out var flags, out _, out var tcpPayload))
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
            if (!route.TryEnqueueIncoming(ipv4Packet))
            {
                if (ZeroTierTrace.Enabled)
                {
                    ZeroTierTrace.WriteLine($"[zerotier] Drop+RST: TCP route backlog overflow for {dst}:{dstPort} <- {src}:{srcPort} (drops={route.IncomingDropCount}).");
                }

                var ackIncrement = 0u;
                if ((flags & TcpCodec.Flags.Syn) != 0)
                {
                    ackIncrement++;
                }

                if ((flags & TcpCodec.Flags.Fin) != 0)
                {
                    ackIncrement++;
                }

                try
                {
                    await SendTcpRstAsync(
                            peerNodeId,
                            localIp: dst,
                            remoteIp: src,
                            localPort: dstPort,
                            remotePort: srcPort,
                            acknowledgmentNumber: unchecked(seq + (uint)tcpPayload.Length + ackIncrement),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or IOException)
                {
                }
            }

            return;
        }

        if ((flags & TcpCodec.Flags.Syn) != 0 &&
            (flags & TcpCodec.Flags.Ack) == 0 &&
            _routes.TryGetTcpSynHandler(AddressFamily.InterNetwork, destinationIp: dst, dstPort, out var handler))
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
        if (_localManagedIpsV4Bytes.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        if (!ZeroTierArp.TryParseRequest(frame.Span, out var senderMac, out var senderIp, out var targetIp))
        {
            return ValueTask.CompletedTask;
        }

        _managedIpToNodeId.LearnFromNeighbor(new IPAddress(senderIp), peerNodeId);

        for (var i = 0; i < _localManagedIpsV4Bytes.Length; i++)
        {
            var localManagedIpV4Bytes = _localManagedIpsV4Bytes[i];
            if (!targetIp.SequenceEqual(localManagedIpV4Bytes))
            {
                continue;
            }

            var reply = ZeroTierArp.BuildReply(_localMac, localManagedIpV4Bytes, senderMac, senderIp);
            return _sender.SendEthernetFrameAsync(peerNodeId, ZeroTierFrameCodec.EtherTypeArp, reply, cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask SendTcpRstAsync(
        NodeId peerNodeId,
        IPAddress localIp,
        IPAddress remoteIp,
        ushort localPort,
        ushort remotePort,
        uint acknowledgmentNumber,
        CancellationToken cancellationToken)
        => _tcpRst.SendAsync(peerNodeId, localIp, remoteIp, localPort, remotePort, acknowledgmentNumber, cancellationToken);

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

    private bool TryGetLocalManagedIpv4(IPAddress address, out IPAddress localIp)
    {
        if (_localManagedIpsV4.Length == 0 || address.AddressFamily != AddressFamily.InterNetwork)
        {
            localIp = IPAddress.None;
            return false;
        }

        for (var i = 0; i < _localManagedIpsV4.Length; i++)
        {
            var ip = _localManagedIpsV4[i];
            if (address.Equals(ip))
            {
                localIp = ip;
                return true;
            }
        }

        localIp = IPAddress.None;
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
