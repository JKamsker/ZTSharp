using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneIcmpv6Handler
{
    private readonly ZeroTierDataplaneRuntime _sender;
    private readonly ZeroTierMac _localMac;
    private readonly IPAddress[] _localManagedIpsV6;

    public ZeroTierDataplaneIcmpv6Handler(ZeroTierDataplaneRuntime sender, ZeroTierMac localMac, IPAddress[] localManagedIpsV6)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(localManagedIpsV6);

        _sender = sender;
        _localMac = localMac;
        _localManagedIpsV6 = localManagedIpsV6;
    }

    public async ValueTask HandleAsync(
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
            if (Icmpv6Codec.ComputeChecksum(sourceIp, destinationIp, icmpSpan) != 0)
            {
                return;
            }

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

            await _sender.SendEthernetFrameAsync(peerNodeId, ZeroTierFrameCodec.EtherTypeIpv6, packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Neighbor Solicitation
        if (type == 135 && code == 0)
        {
            if (Icmpv6Codec.ComputeChecksum(sourceIp, destinationIp, icmpSpan) != 0)
            {
                return;
            }

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

            await _sender.SendEthernetFrameAsync(peerNodeId, ZeroTierFrameCodec.EtherTypeIpv6, packet, cancellationToken).ConfigureAwait(false);
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
}

