using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierDecryptingPacketReceiver
{
    public static async Task<(NodeId Source, IPEndPoint RemoteEndPoint, byte[] PacketBytes)?> ReceiveAndDecryptAsync(
        ZeroTierUdpTransport udp,
        NodeId expectedSource,
        byte[] key,
        CancellationToken cancellationToken)
    {
        var datagram = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);

        var packetBytes = datagram.Payload;
        if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var packet))
        {
            return null;
        }

        if (packet.Header.Source != expectedSource)
        {
            return null;
        }

        if (!ZeroTierPacketCrypto.Dearmor(packetBytes, key))
        {
            return null;
        }

        if ((packetBytes[ZeroTierPacketHeader.IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
        {
            if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
            {
                return null;
            }

            packetBytes = uncompressed;
        }

        return (packet.Header.Source, datagram.RemoteEndPoint, packetBytes);
    }
}
