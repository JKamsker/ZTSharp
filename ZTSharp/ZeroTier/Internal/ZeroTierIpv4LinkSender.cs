using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierIpv4LinkSender
{
    private readonly ZeroTierUdpTransport _udp;
    private readonly IPEndPoint _relayEndpoint;
    private readonly ZeroTierDirectEndpointManager _directEndpoints;
    private readonly NodeId _localNodeId;
    private readonly NodeId _remoteNodeId;
    private readonly ulong _networkId;
    private readonly byte[] _inlineCom;
    private readonly ZeroTierMac _to;
    private readonly ZeroTierMac _from;
    private readonly byte[] _sharedKey;
    private readonly byte _remoteProtocolVersion;
    private int _traceTxRemaining = 20;

    public ZeroTierIpv4LinkSender(
        ZeroTierUdpTransport udp,
        IPEndPoint relayEndpoint,
        ZeroTierDirectEndpointManager directEndpoints,
        NodeId localNodeId,
        NodeId remoteNodeId,
        ulong networkId,
        byte[] inlineCom,
        ZeroTierMac to,
        ZeroTierMac from,
        byte[] sharedKey,
        byte remoteProtocolVersion)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(relayEndpoint);
        ArgumentNullException.ThrowIfNull(directEndpoints);
        ArgumentNullException.ThrowIfNull(inlineCom);
        ArgumentNullException.ThrowIfNull(sharedKey);

        _udp = udp;
        _relayEndpoint = relayEndpoint;
        _directEndpoints = directEndpoints;
        _localNodeId = localNodeId;
        _remoteNodeId = remoteNodeId;
        _networkId = networkId;
        _inlineCom = inlineCom;
        _to = to;
        _from = from;
        _sharedKey = sharedKey;
        _remoteProtocolVersion = remoteProtocolVersion;
    }

    public async ValueTask SendIpv4Async(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var packet = ZeroTierExtFramePacketBuilder.BuildIpv4Packet(
            packetId,
            destination: _remoteNodeId,
            source: _localNodeId,
            networkId: _networkId,
            inlineCom: _inlineCom,
            to: _to,
            from: _from,
            ipv4Packet: ipPacket.Span,
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

    public Task SendExtFrameAsync(ushort etherType, ReadOnlySpan<byte> frame, CancellationToken cancellationToken)
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
}

