using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using JKamsker.LibZt.ZeroTier.Protocol;
using JKamsker.LibZt.ZeroTier.Transport;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal static class ZtZeroTierWhoisClient
{
    private const int IndexVerb = 27;

    private const int OkIndexInReVerb = ZtZeroTierPacketHeader.Length;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

    public static async Task<ZtZeroTierIdentity> WhoisAsync(
        ZtZeroTierUdpTransport udp,
        ZtNodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        ZtNodeId localNodeId,
        ZtNodeId targetNodeId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        var payload = new byte[5];
        WriteUInt40(payload, targetNodeId.Value);

        var packetId = GeneratePacketId();
        var header = new ZtZeroTierPacketHeader(
            PacketId: packetId,
            Destination: rootNodeId,
            Source: localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZtZeroTierVerb.Whois);

        var packet = ZtZeroTierPacketCodec.Encode(header, payload);
        ZtZeroTierPacketCrypto.Armor(packet, rootKey, encryptPayload: true);
        await udp.SendAsync(rootEndpoint, packet, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            ZtZeroTierUdpDatagram datagram;
            try
            {
                datagram = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for WHOIS response after {timeout}.");
            }

            var packetBytes = datagram.Payload.ToArray();
            if (!ZtZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
            {
                continue;
            }

            if (decoded.Header.Source != rootNodeId)
            {
                continue;
            }

            if (!ZtZeroTierPacketCrypto.Dearmor(packetBytes, rootKey))
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
            if (verb != ZtZeroTierVerb.Ok)
            {
                continue;
            }

            if (packetBytes.Length < OkIndexPayload)
            {
                continue;
            }

            var inReVerb = (ZtZeroTierVerb)(packetBytes[OkIndexInReVerb] & 0x1F);
            if (inReVerb != ZtZeroTierVerb.Whois)
            {
                continue;
            }

            var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(OkIndexInRePacketId, 8));
            if (inRePacketId != packetId)
            {
                continue;
            }

            return ZtZeroTierIdentityCodec.Deserialize(packetBytes.AsSpan(OkIndexPayload), out _);
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
}

