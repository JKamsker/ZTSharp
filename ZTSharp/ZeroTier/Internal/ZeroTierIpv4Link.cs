using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierIpv4Link : IUserSpaceIpLink
{
    private const int IndexVerb = 27;
    private const ushort EtherTypeArp = 0x0806;

    private readonly ZeroTierUdpTransport _udp;
    private readonly IPEndPoint _relayEndpoint;
    private readonly NodeId _rootNodeId;
    private readonly NodeId _localNodeId;
    private readonly NodeId _remoteNodeId;
    private readonly ulong _networkId;
    private readonly IPAddress _localManagedIp;
    private readonly byte[] _localManagedIpV4;
    private readonly byte[] _inlineCom;
    private readonly ZeroTierMac _to;
    private readonly ZeroTierMac _from;
    private readonly byte[] _rootKey;
    private readonly byte[] _sharedKey;
    private readonly byte _remoteProtocolVersion;
    private IPEndPoint[] _directEndpoints = Array.Empty<IPEndPoint>();
    private int _traceRxRemaining = 50;
    private int _traceRxVerbRemaining = 50;
    private int _traceTxRemaining = 20;
    private bool _disposed;

    public ZeroTierIpv4Link(
        ZeroTierUdpTransport udp,
        IPEndPoint relayEndpoint,
        NodeId rootNodeId,
        byte[] rootKey,
        NodeId localNodeId,
        NodeId remoteNodeId,
        ulong networkId,
        IPAddress localManagedIp,
        byte[] inlineCom,
        byte[] sharedKey,
        byte remoteProtocolVersion)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(relayEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(localManagedIp);
        ArgumentNullException.ThrowIfNull(inlineCom);
        ArgumentNullException.ThrowIfNull(sharedKey);
        if (localManagedIp.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentOutOfRangeException(nameof(localManagedIp), "Local managed IP must be an IPv4 address.");
        }

        _udp = udp;
        _relayEndpoint = relayEndpoint;
        _rootNodeId = rootNodeId;
        _localNodeId = localNodeId;
        _remoteNodeId = remoteNodeId;
        _networkId = networkId;
        _localManagedIp = localManagedIp;
        _localManagedIpV4 = localManagedIp.GetAddressBytes();
        _inlineCom = inlineCom;
        _to = ZeroTierMac.FromAddress(remoteNodeId, networkId);
        _from = ZeroTierMac.FromAddress(localNodeId, networkId);
        _rootKey = rootKey;
        _sharedKey = sharedKey;
        _remoteProtocolVersion = remoteProtocolVersion;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var ipv4Packet = ipPacket;
        var packetId = GeneratePacketId();
        var packet = ZeroTierExtFramePacketBuilder.BuildIpv4Packet(
            packetId,
            destination: _remoteNodeId,
            source: _localNodeId,
            networkId: _networkId,
            inlineCom: _inlineCom,
            to: _to,
            from: _from,
            ipv4Packet: ipv4Packet.Span,
            sharedKey: _sharedKey,
            remoteProtocolVersion: _remoteProtocolVersion);

        var directEndpoints = _directEndpoints;
        if (ZeroTierTrace.Enabled && _traceTxRemaining > 0)
        {
            _traceTxRemaining--;
            ZeroTierTrace.WriteLine($"[zerotier] TX EXT_FRAME: direct={FormatEndpoints(directEndpoints)} relay={_relayEndpoint}.");
        }

        foreach (var endpoint in directEndpoints)
        {
            if (endpoint.Equals(_relayEndpoint))
            {
                continue;
            }

            await _udp.SendAsync(endpoint, packet, cancellationToken).ConfigureAwait(false);
        }

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

            if ((packetBytes[IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
            {
                if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
                {
                    continue;
                }

                packetBytes = uncompressed;
            }

            var verb = (ZeroTierVerb)(packetBytes[IndexVerb] & 0x1F);
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

            var payload = packetBytes.AsSpan(ZeroTierPacketHeader.Length);

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

                        var inReVerb = (ZeroTierVerb)(payload[0] & 0x1F);
                        var errorCode = payload[1 + 8];
                        ulong? networkId = null;
                        if (payload.Length >= 1 + 8 + 1 + 8)
                        {
                            networkId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1 + 8 + 1, 8));
                        }

                        throw new InvalidOperationException(FormatError(inReVerb, errorCode, networkId));
                    }
                case ZeroTierVerb.Rendezvous when isFromRoot:
                    {
                        if (ZeroTierRendezvousCodec.TryParse(payload, out var rendezvous) && rendezvous.With == _remoteNodeId)
                        {
                            var endpoints = NormalizeDirectEndpoints([rendezvous.Endpoint], maxEndpoints: 8);
                            if (ZeroTierTrace.Enabled)
                            {
                                ZeroTierTrace.WriteLine($"[zerotier] RX RENDEZVOUS: {rendezvous.With} endpoints: {FormatEndpoints(endpoints)} via {datagram.RemoteEndPoint}.");
                            }

                            _directEndpoints = endpoints;

                            foreach (var endpoint in endpoints)
                            {
                                await SendHolePunchAsync(endpoint, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            ZeroTierTrace.WriteLine($"[zerotier] RX RENDEZVOUS (ignored) via {datagram.RemoteEndPoint}.");
                        }

                        continue;
                    }
                case ZeroTierVerb.PushDirectPaths when isFromRemote:
                    {
                        if (!ZeroTierPushDirectPathsCodec.TryParse(payload, out var paths) || paths.Length == 0)
                        {
                            ZeroTierTrace.WriteLine("[zerotier] Drop: failed to parse PUSH_DIRECT_PATHS payload.");
                            continue;
                        }

                        var endpoints = NormalizeDirectEndpoints(paths.Select(p => p.Endpoint), maxEndpoints: 8);
                        if (endpoints.Length == 0)
                        {
                            continue;
                        }

                        if (ZeroTierTrace.Enabled)
                        {
                            ZeroTierTrace.WriteLine($"[zerotier] RX PUSH_DIRECT_PATHS: endpoints: {FormatEndpoints(endpoints)} (candidates: {paths.Length}).");
                        }

                        _directEndpoints = endpoints;

                        foreach (var endpoint in endpoints)
                        {
                            await SendHolePunchAsync(endpoint, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }
                case ZeroTierVerb.MulticastFrame:
                    {
                        if (!isFromRemote)
                        {
                            continue;
                        }

                        if (!TryParseMulticastFramePayload(payload, out var networkId, out var etherType, out var frame))
                        {
                            ZeroTierTrace.WriteLine("[zerotier] Drop: failed to parse MULTICAST_FRAME payload.");
                            continue;
                        }

                        if (networkId != _networkId)
                        {
                            continue;
                        }

                        if (etherType == EtherTypeArp)
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

                        if (!ZeroTierFrameCodec.TryParseFramePayload(payload, out var networkId, out var etherType, out var frame))
                        {
                            ZeroTierTrace.WriteLine("[zerotier] Drop: failed to parse FRAME payload.");
                            continue;
                        }

                        if (networkId != _networkId)
                        {
                            ZeroTierTrace.WriteLine($"[zerotier] Drop: FRAME network/ethertype mismatch (networkId=0x{networkId:x16}, etherType=0x{etherType:x4}).");
                            continue;
                        }

                        if (etherType == EtherTypeArp)
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
                                payload,
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

                        if (etherType == EtherTypeArp)
                        {
                            await HandleArpFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (etherType != ZeroTierFrameCodec.EtherTypeIpv4)
                        {
                            ZeroTierTrace.WriteLine($"[zerotier] Drop: EXT_FRAME network/ethertype mismatch (networkId=0x{networkId:x16}, etherType=0x{etherType:x4}).");
                            continue;
                        }

                        if (to != _from || from != _to)
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

    private async ValueTask SendHolePunchAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var junk = new byte[4];
        RandomNumberGenerator.Fill(junk);

        try
        {
            ZeroTierTrace.WriteLine($"[zerotier] TX hole-punch to {endpoint}.");
            await _udp.SendAsync(endpoint, junk, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
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

    private ValueTask HandleArpFrameAsync(ReadOnlySpan<byte> frame, CancellationToken cancellationToken)
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
        return new ValueTask(SendExtFrameAsync(EtherTypeArp, reply, cancellationToken));
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

        if (htype != 1 || ptype != ZeroTierFrameCodec.EtherTypeIpv4 || hlen != 6 || plen != 4 || oper != 1)
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
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), ZeroTierFrameCodec.EtherTypeIpv4); // PTYPE IPv4
        span[4] = 6; // HLEN
        span[5] = 4; // PLEN
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(6, 2), 2); // OPER reply

        Span<byte> localMac = stackalloc byte[6];
        _from.CopyTo(localMac);

        localMac.CopyTo(span.Slice(8, 6)); // SHA
        _localManagedIpV4.CopyTo(span.Slice(14, 4)); // SPA
        requesterMac.CopyTo(span.Slice(18, 6)); // THA
        requesterIp.CopyTo(span.Slice(24, 4)); // TPA

        return reply;
    }

    private Task SendExtFrameAsync(ushort etherType, ReadOnlySpan<byte> frame, CancellationToken cancellationToken)
    {
        var packetId = GeneratePacketId();
        var packet = BuildExtFramePacket(packetId, etherType, frame);

        var directEndpoints = _directEndpoints;
        List<Task>? tasks = null;

        foreach (var endpoint in directEndpoints)
        {
            if (endpoint.Equals(_relayEndpoint))
            {
                continue;
            }

            tasks ??= new List<Task>(Math.Min(directEndpoints.Length, 8) + 1);
            tasks.Add(_udp.SendAsync(endpoint, packet, cancellationToken));
        }

        if (tasks is null)
        {
            return _udp.SendAsync(_relayEndpoint, packet, cancellationToken);
        }

        tasks.Add(_udp.SendAsync(_relayEndpoint, packet, cancellationToken));
        return Task.WhenAll(tasks);
    }

    private byte[] BuildExtFramePacket(ulong packetId, ushort etherType, ReadOnlySpan<byte> frame)
    {
        var extFrameFlags = (byte)(0x01 | (ZeroTierTrace.Enabled ? 0x10 : 0x00));
        var payload = ZeroTierFrameCodec.EncodeExtFramePayload(
            _networkId,
            extFrameFlags,
            inlineCom: _inlineCom,
            to: _to,
            from: _from,
            etherType,
            frame);

        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: _remoteNodeId,
            Source: _localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.ExtFrame);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, ZeroTierPacketCrypto.SelectOutboundKey(_sharedKey, _remoteProtocolVersion), encryptPayload: true);
        return packet;
    }

    private IPEndPoint[] NormalizeDirectEndpoints(IEnumerable<IPEndPoint> endpoints, int maxEndpoints)
    {
        var publicV4 = new List<IPEndPoint>();
        var publicV6 = new List<IPEndPoint>();
        var privateV4 = new List<IPEndPoint>();
        var privateV6 = new List<IPEndPoint>();

        foreach (var endpoint in endpoints)
        {
            if (endpoint.Port is < 1 or > ushort.MaxValue)
            {
                continue;
            }

            if (endpoint.Equals(_relayEndpoint))
            {
                continue;
            }

            var isPublic = IsPublicAddress(endpoint.Address);
            if (endpoint.AddressFamily == AddressFamily.InterNetwork)
            {
                (isPublic ? publicV4 : privateV4).Add(endpoint);
            }
            else if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                (isPublic ? publicV6 : privateV6).Add(endpoint);
            }
        }

        var ordered = publicV4
            .Concat(publicV6)
            .Concat(privateV4)
            .Concat(privateV6);

        var unique = new List<IPEndPoint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in ordered)
        {
            var key = endpoint.Address + ":" + endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!seen.Add(key))
            {
                continue;
            }

            unique.Add(endpoint);
            if (unique.Count >= maxEndpoints)
            {
                break;
            }
        }

        return unique.ToArray();
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
            {
                return false;
            }

            if (bytes[0] == 10)
            {
                return false;
            }

            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            {
                return false;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return false;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return false;
            }

            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            {
                return false;
            }

            if (bytes[0] == 0 || bytes[0] >= 224)
            {
                return false;
            }

            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal ||
                address.IsIPv6Multicast ||
                address.IsIPv6SiteLocal ||
                address.Equals(IPAddress.IPv6Loopback))
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            if (bytes.Length != 16)
            {
                return false;
            }

            // fc00::/7 Unique Local Address (ULA)
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static string FormatEndpoints(IPEndPoint[] endpoints)
    {
        if (endpoints.Length == 0)
        {
            return "<none>";
        }

        return string.Join(", ", endpoints.Select(endpoint => endpoint.ToString()));
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

    private static string FormatError(ZeroTierVerb inReVerb, byte errorCode, ulong? networkId)
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
            ZeroTierVerb.ExtFrame => "ERROR(EXT_FRAME)",
            ZeroTierVerb.Frame => "ERROR(FRAME)",
            _ => $"ERROR({inReVerb})"
        };

        return networkId is null
            ? $"{prefix}: {message}"
            : $"{prefix}: {message} (network: 0x{networkId:x16})";
    }
}
