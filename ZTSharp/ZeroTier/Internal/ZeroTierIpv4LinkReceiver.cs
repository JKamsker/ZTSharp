using System.Buffers.Binary;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierIpv4LinkReceiver
{
    private readonly ZeroTierUdpTransport _udp;
    private readonly NodeId _rootNodeId;
    private readonly NodeId _localNodeId;
    private readonly NodeId _remoteNodeId;
    private readonly ulong _networkId;
    private readonly byte[] _rootKey;
    private readonly byte[] _sharedKey;
    private readonly ZeroTierMac _localMac;
    private readonly ZeroTierMac _remoteMac;
    private readonly byte[] _localManagedIpV4;
    private readonly ZeroTierDirectEndpointManager _directEndpoints;
    private readonly ZeroTierIpv4LinkSender _sender;

    private int _traceRxRemaining = 50;
    private int _traceRxVerbRemaining = 50;

    public ZeroTierIpv4LinkReceiver(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        NodeId localNodeId,
        NodeId remoteNodeId,
        ulong networkId,
        byte[] rootKey,
        byte[] sharedKey,
        ZeroTierMac localMac,
        ZeroTierMac remoteMac,
        byte[] localManagedIpV4,
        ZeroTierDirectEndpointManager directEndpoints,
        ZeroTierIpv4LinkSender sender)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(sharedKey);
        ArgumentNullException.ThrowIfNull(localManagedIpV4);
        ArgumentNullException.ThrowIfNull(directEndpoints);
        ArgumentNullException.ThrowIfNull(sender);

        _udp = udp;
        _rootNodeId = rootNodeId;
        _localNodeId = localNodeId;
        _remoteNodeId = remoteNodeId;
        _networkId = networkId;
        _rootKey = rootKey;
        _sharedKey = sharedKey;
        _localMac = localMac;
        _remoteMac = remoteMac;
        _localManagedIpV4 = localManagedIpV4;
        _directEndpoints = directEndpoints;
        _sender = sender;
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var datagram = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            var packetBytes = datagram.Payload.ToArray();
            if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
            {
                continue;
            }

            if (ZeroTierTrace.Enabled && decoded.Header.Destination == _localNodeId && _traceRxRemaining > 0)
            {
                _traceRxRemaining--;
                ZeroTierTrace.WriteLine($"[zerotier] RX raw: src={decoded.Header.Source} dst={decoded.Header.Destination} flags=0x{decoded.Header.Flags:x2} verbRaw=0x{decoded.Header.VerbRaw:x2} via {datagram.RemoteEndPoint}.");
            }

            if (decoded.Header.Destination != _localNodeId)
            {
                continue;
            }

            var isFromRemote = decoded.Header.Source == _remoteNodeId;
            var isFromRoot = decoded.Header.Source == _rootNodeId;
            if (!isFromRemote && !isFromRoot)
            {
                continue;
            }

            var key = isFromRemote ? _sharedKey : _rootKey;
            if (!ZeroTierPacketCrypto.Dearmor(packetBytes, key))
            {
                if (isFromRoot)
                {
                    ZeroTierTrace.WriteLine($"[zerotier] Drop: failed to dearmor packet from root {_rootNodeId} via {datagram.RemoteEndPoint}.");
                }
                else if (ZeroTierTrace.Enabled)
                {
                    ZeroTierTrace.WriteLine($"[zerotier] Drop: failed to dearmor packet from {_remoteNodeId} via {datagram.RemoteEndPoint}.");
                }

                continue;
            }

            if ((packetBytes[ZeroTierPacketHeader.IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
            {
                if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
                {
                    continue;
                }

                packetBytes = uncompressed;
            }

            var verb = (ZeroTierVerb)(packetBytes[ZeroTierPacketHeader.IndexVerb] & 0x1F);
            if (packetBytes.Length < ZeroTierPacketHeader.Length)
            {
                continue;
            }

            if (ZeroTierTrace.Enabled && _traceRxVerbRemaining > 0)
            {
                _traceRxVerbRemaining--;
                var from = isFromRoot ? $"root {_rootNodeId}" : $"peer {_remoteNodeId}";
                ZeroTierTrace.WriteLine($"[zerotier] RX {verb} from {from} via {datagram.RemoteEndPoint}.");
            }

            var payload = packetBytes.AsMemory(ZeroTierPacketHeader.Length);

            switch (verb)
            {
                case ZeroTierVerb.Error:
                    {
                        if (isFromRoot)
                        {
                            ZeroTierTrace.WriteLine($"[zerotier] RX ERROR from root {_rootNodeId} via {datagram.RemoteEndPoint}.");
                        }

                        if (payload.Length < 1 + 8 + 1)
                        {
                            continue;
                        }

                        var payloadSpan = payload.Span;
                        var inReVerb = (ZeroTierVerb)(payloadSpan[0] & 0x1F);
                        var errorCode = payloadSpan[1 + 8];
                        ulong? networkId = null;
                        if (payloadSpan.Length >= 1 + 8 + 1 + 8)
                        {
                            networkId = BinaryPrimitives.ReadUInt64BigEndian(payloadSpan.Slice(1 + 8 + 1, 8));
                        }

                        throw new InvalidOperationException(ZeroTierErrorFormatting.FormatError(inReVerb, errorCode, networkId));
                    }
                case ZeroTierVerb.Rendezvous when isFromRoot:
                    {
                        await _directEndpoints
                            .HandleRendezvousFromRootAsync(payload, datagram.RemoteEndPoint, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }
                case ZeroTierVerb.PushDirectPaths when isFromRemote:
                    {
                        await _directEndpoints.HandlePushDirectPathsFromRemoteAsync(payload, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                case ZeroTierVerb.MulticastFrame:
                    {
                        if (!isFromRemote)
                        {
                            continue;
                        }

                        if (!ZeroTierMulticastFramePayload.TryParse(payload.Span, out var networkId, out var etherType, out var frame))
                        {
                            ZeroTierTrace.WriteLine("[zerotier] Drop: failed to parse MULTICAST_FRAME payload.");
                            continue;
                        }

                        if (networkId != _networkId)
                        {
                            continue;
                        }

                        if (etherType == ZeroTierFrameCodec.EtherTypeArp)
                        {
                            await HandleArpFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }
                case ZeroTierVerb.Frame:
                    {
                        if (!isFromRemote)
                        {
                            continue;
                        }

                        if (!ZeroTierFrameCodec.TryParseFramePayload(payload.Span, out var networkId, out var etherType, out var frame))
                        {
                            ZeroTierTrace.WriteLine("[zerotier] Drop: failed to parse FRAME payload.");
                            continue;
                        }

                        if (networkId != _networkId)
                        {
                            ZeroTierTrace.WriteLine($"[zerotier] Drop: FRAME network/ethertype mismatch (networkId=0x{networkId:x16}, etherType=0x{etherType:x4}).");
                            continue;
                        }

                        if (etherType == ZeroTierFrameCodec.EtherTypeArp)
                        {
                            await HandleArpFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (etherType != ZeroTierFrameCodec.EtherTypeIpv4)
                        {
                            ZeroTierTrace.WriteLine($"[zerotier] Drop: FRAME network/ethertype mismatch (networkId=0x{networkId:x16}, etherType=0x{etherType:x4}).");
                            continue;
                        }

                        return packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
                    }
                case ZeroTierVerb.ExtFrame:
                    {
                        if (!isFromRemote)
                        {
                            continue;
                        }

                        if (!ZeroTierFrameCodec.TryParseExtFramePayload(
                                payload.Span,
                                out var networkId,
                                out _,
                                out _,
                                out var to,
                                out var from,
                                out var etherType,
                                out var frame))
                        {
                            ZeroTierTrace.WriteLine("[zerotier] Drop: failed to parse EXT_FRAME payload.");
                            continue;
                        }

                        if (networkId != _networkId)
                        {
                            ZeroTierTrace.WriteLine($"[zerotier] Drop: EXT_FRAME network/ethertype mismatch (networkId=0x{networkId:x16}, etherType=0x{etherType:x4}).");
                            continue;
                        }

                        if (etherType == ZeroTierFrameCodec.EtherTypeArp)
                        {
                            await HandleArpFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (etherType != ZeroTierFrameCodec.EtherTypeIpv4)
                        {
                            ZeroTierTrace.WriteLine($"[zerotier] Drop: EXT_FRAME network/ethertype mismatch (networkId=0x{networkId:x16}, etherType=0x{etherType:x4}).");
                            continue;
                        }

                        if (to != _localMac || from != _remoteMac)
                        {
                            ZeroTierTrace.WriteLine($"[zerotier] Drop: EXT_FRAME to/from mismatch (to={to}, from={from}).");
                            continue;
                        }

                        return packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
                    }
                default:
                    continue;
            }
        }
    }

    private ValueTask HandleArpFrameAsync(ReadOnlySpan<byte> frame, CancellationToken cancellationToken)
    {
        if (!ZeroTierArp.TryParseRequest(frame, out var senderMac, out var senderIp, out var targetIp))
        {
            return ValueTask.CompletedTask;
        }

        if (!targetIp.SequenceEqual(_localManagedIpV4))
        {
            return ValueTask.CompletedTask;
        }

        var reply = ZeroTierArp.BuildReply(_localMac, _localManagedIpV4, senderMac, senderIp);
        return new ValueTask(_sender.SendExtFrameAsync(ZeroTierFrameCodec.EtherTypeArp, reply, cancellationToken));
    }
}
