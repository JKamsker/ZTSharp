using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using JKamsker.LibZt.ZeroTier.Net;
using JKamsker.LibZt.ZeroTier.Protocol;
using JKamsker.LibZt.ZeroTier.Transport;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal sealed class ZtZeroTierIpv4Link : IZtUserSpaceIpv4Link
{
    private const int IndexVerb = 27;

    private readonly ZtZeroTierUdpTransport _udp;
    private readonly IPEndPoint _relayEndpoint;
    private readonly ZtNodeId _localNodeId;
    private readonly ZtNodeId _remoteNodeId;
    private readonly ulong _networkId;
    private readonly byte[] _inlineCom;
    private readonly ZtZeroTierMac _to;
    private readonly ZtZeroTierMac _from;
    private readonly byte[] _sharedKey;
    private bool _disposed;

    public ZtZeroTierIpv4Link(
        ZtZeroTierUdpTransport udp,
        IPEndPoint relayEndpoint,
        ZtNodeId localNodeId,
        ZtNodeId remoteNodeId,
        ulong networkId,
        byte[] inlineCom,
        byte[] sharedKey)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(relayEndpoint);
        ArgumentNullException.ThrowIfNull(inlineCom);
        ArgumentNullException.ThrowIfNull(sharedKey);

        _udp = udp;
        _relayEndpoint = relayEndpoint;
        _localNodeId = localNodeId;
        _remoteNodeId = remoteNodeId;
        _networkId = networkId;
        _inlineCom = inlineCom;
        _to = ZtZeroTierMac.FromAddress(remoteNodeId, networkId);
        _from = ZtZeroTierMac.FromAddress(localNodeId, networkId);
        _sharedKey = sharedKey;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var packetId = GeneratePacketId();
        var packet = ZtZeroTierExtFramePacketBuilder.BuildIpv4Packet(
            packetId,
            destination: _remoteNodeId,
            source: _localNodeId,
            networkId: _networkId,
            inlineCom: _inlineCom,
            to: _to,
            from: _from,
            ipv4Packet: ipv4Packet.Span,
            sharedKey: _sharedKey);

        await _udp.SendAsync(_relayEndpoint, packet, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            var datagram = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            var packetBytes = datagram.Payload.ToArray();
            if (!ZtZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
            {
                continue;
            }

            if (decoded.Header.Destination != _localNodeId || decoded.Header.Source != _remoteNodeId)
            {
                continue;
            }

            if (!ZtZeroTierPacketCrypto.Dearmor(packetBytes, _sharedKey))
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
            if (packetBytes.Length < ZtZeroTierPacketHeader.Length)
            {
                continue;
            }

            var payload = packetBytes.AsSpan(ZtZeroTierPacketHeader.Length);

            switch (verb)
            {
                case ZtZeroTierVerb.Error:
                {
                    if (payload.Length < 1 + 8 + 1)
                    {
                        continue;
                    }

                    var inReVerb = (ZtZeroTierVerb)(payload[0] & 0x1F);
                    var errorCode = payload[1 + 8];
                    ulong? networkId = null;
                    if (payload.Length >= 1 + 8 + 1 + 8)
                    {
                        networkId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1 + 8 + 1, 8));
                    }

                    throw new InvalidOperationException(FormatError(inReVerb, errorCode, networkId));
                }
                case ZtZeroTierVerb.Frame:
                {
                    if (!ZtZeroTierFrameCodec.TryParseFramePayload(payload, out var networkId, out var etherType, out var frame))
                    {
                        continue;
                    }

                    if (networkId != _networkId || etherType != ZtZeroTierFrameCodec.EtherTypeIpv4)
                    {
                        continue;
                    }

                    return packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
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
                        continue;
                    }

                    if (networkId != _networkId || etherType != ZtZeroTierFrameCodec.EtherTypeIpv4)
                    {
                        continue;
                    }

                    if (to != _from || from != _to)
                    {
                        continue;
                    }

                    return packetBytes.AsMemory(packetBytes.Length - frame.Length, frame.Length);
                }
                default:
                    continue;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _udp.DisposeAsync().ConfigureAwait(false);
    }

    private static ulong GeneratePacketId()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
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
            ZtZeroTierVerb.ExtFrame => "ERROR(EXT_FRAME)",
            ZtZeroTierVerb.Frame => "ERROR(FRAME)",
            _ => $"ERROR({inReVerb})"
        };

        return networkId is null
            ? $"{prefix}: {message}"
            : $"{prefix}: {message} (network: 0x{networkId:x16})";
    }
}
