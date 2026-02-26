using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierPeerEchoManager
{
    private const long EchoMinIntervalMs = 5_000;
    private const long PendingEchoTtlMs = 30_000;

    private readonly IZeroTierUdpTransport _udp;
    private readonly NodeId _localNodeId;
    private readonly Func<NodeId, byte> _getPeerProtocolVersion;
    private readonly Func<long> _nowUnixMs;

    private readonly ConcurrentDictionary<ZeroTierPeerEchoPathKey, long> _lastEchoSentUnixMs = new();
    private readonly ConcurrentDictionary<ulong, PendingEcho> _pendingByPacketId = new();
    private readonly ConcurrentDictionary<ZeroTierPeerEchoPathKey, int> _lastRttMsByPath = new();
    private long _lastPendingCleanupUnixMs;

    public ZeroTierPeerEchoManager(
        IZeroTierUdpTransport udp,
        NodeId localNodeId,
        Func<NodeId, byte> getPeerProtocolVersion,
        Func<long>? nowUnixMs = null)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(getPeerProtocolVersion);

        _udp = udp;
        _localNodeId = localNodeId;
        _getPeerProtocolVersion = getPeerProtocolVersion;
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public bool TryGetLastRttMs(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint, out int rttMs)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        var key = new ZeroTierPeerEchoPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        return _lastRttMsByPath.TryGetValue(key, out rttMs);
    }

    public async ValueTask TrySendEchoProbeAsync(
        NodeId peerNodeId,
        int localSocketId,
        IPEndPoint remoteEndPoint,
        byte[] sharedKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(sharedKey);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _nowUnixMs();
        CleanupPendingIfNeeded(now);

        var pathKey = new ZeroTierPeerEchoPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        if (_lastEchoSentUnixMs.TryGetValue(pathKey, out var lastSent) && unchecked(now - lastSent) < EchoMinIntervalMs)
        {
            return;
        }

        _lastEchoSentUnixMs[pathKey] = now;

        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(payload, (ulong)now);

        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: peerNodeId,
            Source: _localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Echo);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        var peerProtocolVersion = _getPeerProtocolVersion(peerNodeId);
        ZeroTierPacketCrypto.Armor(packet, ZeroTierPacketCrypto.SelectOutboundKey(sharedKey, peerProtocolVersion), encryptPayload: true);
        var onWirePacketId = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(0, 8));

        _pendingByPacketId[onWirePacketId] = new PendingEcho(pathKey, TimestampUnixMs: now);
        try
        {
            await _udp.SendAsync(localSocketId, remoteEndPoint, packet, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pendingByPacketId.TryRemove(onWirePacketId, out _);
            throw;
        }
    }

    public async ValueTask HandleEchoRequestAsync(
        NodeId peerNodeId,
        int localSocketId,
        IPEndPoint remoteEndPoint,
        ulong inRePacketId,
        ReadOnlyMemory<byte> payload,
        byte[] sharedKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(sharedKey);
        cancellationToken.ThrowIfCancellationRequested();

        var payloadSpan = payload.Span;
        if (payloadSpan.Length < 8)
        {
            return;
        }

        var timestampEcho = BinaryPrimitives.ReadUInt64BigEndian(payloadSpan.Slice(0, 8));

        var okPayload = new byte[1 + 8 + 8];
        okPayload[0] = (byte)ZeroTierVerb.Echo;
        BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(1, 8), inRePacketId);
        BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(1 + 8, 8), timestampEcho);

        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: peerNodeId,
            Source: _localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Ok);

        var packet = ZeroTierPacketCodec.Encode(header, okPayload);
        var peerProtocolVersion = _getPeerProtocolVersion(peerNodeId);
        ZeroTierPacketCrypto.Armor(packet, ZeroTierPacketCrypto.SelectOutboundKey(sharedKey, peerProtocolVersion), encryptPayload: true);
        await _udp.SendAsync(localSocketId, remoteEndPoint, packet, cancellationToken).ConfigureAwait(false);
    }

    public void HandleEchoOk(
        NodeId peerNodeId,
        int localSocketId,
        IPEndPoint remoteEndPoint,
        ulong inRePacketId,
        ReadOnlySpan<byte> okPayloadTail)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        if (!_pendingByPacketId.TryRemove(inRePacketId, out var pending))
        {
            return;
        }

        if (pending.PathKey.PeerNodeId != peerNodeId ||
            pending.PathKey.Path.LocalSocketId != localSocketId ||
            !pending.PathKey.Path.RemoteEndPoint.Equals(remoteEndPoint))
        {
            return;
        }

        if (okPayloadTail.Length < 8)
        {
            return;
        }

        var timestampEcho = BinaryPrimitives.ReadUInt64BigEndian(okPayloadTail.Slice(0, 8));
        var now = _nowUnixMs();
        var rtt = unchecked((long)now - (long)timestampEcho);
        if (rtt < 0 || rtt > int.MaxValue)
        {
            return;
        }

        _lastRttMsByPath[pending.PathKey] = (int)rtt;
    }

    private void CleanupPendingIfNeeded(long nowUnixMs)
    {
        var last = Volatile.Read(ref _lastPendingCleanupUnixMs);
        if (unchecked(nowUnixMs - last) < 1000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastPendingCleanupUnixMs, nowUnixMs, last) != last)
        {
            return;
        }

        var expiresBefore = nowUnixMs - PendingEchoTtlMs;
        foreach (var (packetId, pending) in _pendingByPacketId)
        {
            if (pending.TimestampUnixMs <= expiresBefore)
            {
                _pendingByPacketId.TryRemove(packetId, out _);
            }
        }
    }

    private readonly record struct PendingEcho(ZeroTierPeerEchoPathKey PathKey, long TimestampUnixMs);
}

internal readonly record struct ZeroTierPeerEchoPathKey(NodeId PeerNodeId, ZeroTierPeerPhysicalPathKey Path);
