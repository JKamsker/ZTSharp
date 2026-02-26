using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierTcpRstSender
{
    private readonly ZeroTierDataplaneRuntime _sender;

    public ZeroTierTcpRstSender(ZeroTierDataplaneRuntime sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _sender = sender;
    }

    public async ValueTask SendAsync(
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
}

