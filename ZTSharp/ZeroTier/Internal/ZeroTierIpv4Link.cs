using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
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
    private readonly ZeroTierDirectEndpointManager _directEndpoints;
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
        _directEndpoints = new ZeroTierDirectEndpointManager(udp, relayEndpoint, remoteNodeId);
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var ipv4Packet = ipPacket;
        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
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

        var directEndpoints = _directEndpoints.Endpoints;
        if (ZeroTierTrace.Enabled && _traceTxRemaining > 0)
        {
            _traceTxRemaining--;
            ZeroTierTrace.WriteLine($"[zerotier] TX EXT_FRAME: direct={ZeroTierDirectEndpointSelection.Format(directEndpoints)} relay={_relayEndpoint}.");
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

        var reply = ZeroTierArp.BuildReply(_from, _localManagedIpV4, senderMac, senderIp);
        return new ValueTask(SendExtFrameAsync(EtherTypeArp, reply, cancellationToken));
    }

    private Task SendExtFrameAsync(ushort etherType, ReadOnlySpan<byte> frame, CancellationToken cancellationToken)
    {
        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var packet = BuildExtFramePacket(packetId, etherType, frame);

        var directEndpoints = _directEndpoints.Endpoints;
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _udp.DisposeAsync().ConfigureAwait(false);
    }

}
