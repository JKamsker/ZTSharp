using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplanePeerPacketHandler
{
    private readonly ulong _networkId;
    private readonly ZeroTierMac _localMac;
    private readonly ZeroTierDataplaneIpHandler _ip;

    public ZeroTierDataplanePeerPacketHandler(ulong networkId, ZeroTierMac localMac, ZeroTierDataplaneIpHandler ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        _networkId = networkId;
        _localMac = localMac;
        _ip = ip;
    }

    public async ValueTask HandleAsync(NodeId peerNodeId, byte[] packetBytes, CancellationToken cancellationToken)
    {
        if (packetBytes.Length <= ZeroTierPacketHeader.IndexVerb)
        {
            return;
        }

        var verb = (ZeroTierVerb)(packetBytes[ZeroTierPacketHeader.IndexVerb] & 0x1F);
        var payload = packetBytes.AsMemory(ZeroTierPacketHeader.IndexPayload);

        switch (verb)
        {
            case ZeroTierVerb.MulticastFrame:
                {
                    if (!TryParseMulticastFramePayload(payload.Span, out var networkId, out var etherType, out var frame))
                    {
                        return;
                    }

                    if (networkId != _networkId)
                    {
                        return;
                    }

                    var frameMemory = packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);

                    if (etherType == ZeroTierFrameCodec.EtherTypeArp)
                    {
                        await _ip.HandleArpFrameAsync(peerNodeId, frameMemory, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (etherType == ZeroTierFrameCodec.EtherTypeIpv6)
                    {
                        await _ip.HandleIpv6PacketAsync(peerNodeId, frameMemory, cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }
            case ZeroTierVerb.Frame:
                {
                    if (!ZeroTierFrameCodec.TryParseFramePayload(payload.Span, out var networkId, out var etherType, out var frame))
                    {
                        return;
                    }

                    if (networkId != _networkId)
                    {
                        return;
                    }

                    var frameMemory = packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);

                    if (etherType == ZeroTierFrameCodec.EtherTypeArp)
                    {
                        await _ip.HandleArpFrameAsync(peerNodeId, frameMemory, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (etherType == ZeroTierFrameCodec.EtherTypeIpv4)
                    {
                        await _ip.HandleIpv4PacketAsync(peerNodeId, frameMemory, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (etherType == ZeroTierFrameCodec.EtherTypeIpv6)
                    {
                        await _ip.HandleIpv6PacketAsync(peerNodeId, frameMemory, cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }
            case ZeroTierVerb.ExtFrame:
                {
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

                    var frameMemory = packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);

                    if (etherType == ZeroTierFrameCodec.EtherTypeArp)
                    {
                        await _ip.HandleArpFrameAsync(peerNodeId, frameMemory, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (etherType == ZeroTierFrameCodec.EtherTypeIpv4)
                    {
                        await _ip.HandleIpv4PacketAsync(peerNodeId, frameMemory, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (etherType == ZeroTierFrameCodec.EtherTypeIpv6)
                    {
                        await _ip.HandleIpv6PacketAsync(peerNodeId, frameMemory, cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }
            default:
                return;
        }
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
}
