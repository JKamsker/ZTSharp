using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierPeerQosManager
{
    private const int MaxPacketBytes = 1400;
    private const int RecordBytes = 8 + 2;
    private const int MaxRecordsPerPacket = MaxPacketBytes / RecordBytes;
    private const int MaxPendingRecords = MaxRecordsPerPacket * 3;

    // See: ZeroTierOne node/Constants.hpp
    private const int QosAckDivisor = 0x2;

    // See: ZeroTierOne node/Bond.cpp (_qosSendInterval default = _failoverInterval * 2).
    private const long DefaultQosSendIntervalMs = 10_000;

    // See: ZeroTierOne node/Bond.cpp (qosStatsOut timeout = _qosSendInterval * 3).
    private const long DefaultRecordTimeoutMs = 30_000;

    private readonly Func<long> _nowMs;
    private readonly ConcurrentDictionary<ZeroTierPeerQosPathKey, PathState> _paths = new();

    public ZeroTierPeerQosManager(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public void RecordIncomingPacket(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint, ulong packetId)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        if (!ShouldTrack(packetId))
        {
            return;
        }

        var now = _nowMs();
        var key = new ZeroTierPeerQosPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        var state = _paths.GetOrAdd(key, static _ => new PathState());

        if (Volatile.Read(ref state.InboundCount) >= MaxPendingRecords)
        {
            return;
        }

        state.Inbound.Enqueue(new PendingInboundRecord(packetId, now));
        Interlocked.Increment(ref state.InboundCount);
    }

    public void RecordOutgoingPacket(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint, ulong packetId)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        if (!ShouldTrack(packetId))
        {
            return;
        }

        var now = _nowMs();
        var key = new ZeroTierPeerQosPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        var state = _paths.GetOrAdd(key, static _ => new PathState());

        CleanupOutboundIfNeeded(state, now);

        if (state.OutboundSentMs.Count >= MaxPendingRecords)
        {
            return;
        }

        state.OutboundSentMs.TryAdd(packetId, now);
    }

    public bool TryBuildOutboundPayload(
        NodeId peerNodeId,
        int localSocketId,
        IPEndPoint remoteEndPoint,
        out byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        payload = Array.Empty<byte>();

        var key = new ZeroTierPeerQosPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        if (!_paths.TryGetValue(key, out var state))
        {
            return false;
        }

        var now = _nowMs();
        var pending = Volatile.Read(ref state.InboundCount);
        if (pending <= 0)
        {
            return false;
        }

        var lastSent = Volatile.Read(ref state.LastSentMs);
        if (pending < MaxRecordsPerPacket && lastSent != 0 && unchecked(now - lastSent) < DefaultQosSendIntervalMs)
        {
            return false;
        }

        var buffer = new byte[MaxPacketBytes];
        var span = buffer.AsSpan();
        var written = 0;
        var records = 0;

        while (records < MaxRecordsPerPacket && state.Inbound.TryDequeue(out var record))
        {
            Interlocked.Decrement(ref state.InboundCount);

            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(written, 8), record.PacketId);
            written += 8;

            var holding = unchecked(now - record.ReceivedMs);
            if (holding < 0)
            {
                holding = 0;
            }

            var holdingU16 = holding > ushort.MaxValue ? ushort.MaxValue : (ushort)holding;
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(written, 2), holdingU16);
            written += 2;

            records++;
        }

        if (records <= 0)
        {
            return false;
        }

        Volatile.Write(ref state.LastSentMs, now);
        payload = buffer.AsSpan(0, written).ToArray();
        return true;
    }

    public void ForgetOutgoingPacket(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint, ulong packetId)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        var key = new ZeroTierPeerQosPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        if (_paths.TryGetValue(key, out var state))
        {
            state.OutboundSentMs.TryRemove(packetId, out _);
        }
    }

    public void HandleInboundMeasurement(
        NodeId peerNodeId,
        int localSocketId,
        IPEndPoint remoteEndPoint,
        ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        if (payload.Length < RecordBytes || payload.Length > MaxPacketBytes)
        {
            return;
        }

        var now = _nowMs();
        var key = new ZeroTierPeerQosPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        if (!_paths.TryGetValue(key, out var state))
        {
            return;
        }

        CleanupOutboundIfNeeded(state, now);

        var count = 0;
        long sumLatency = 0;

        for (var ptr = 0; ptr + RecordBytes <= payload.Length && count < MaxRecordsPerPacket; ptr += RecordBytes)
        {
            var id = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(ptr, 8));
            var holdingMs = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(ptr + 8, 2));
            if (!state.OutboundSentMs.TryRemove(id, out var sentMs))
            {
                continue;
            }

            var elapsed = unchecked(now - sentMs);
            var sample = (elapsed - holdingMs) / 2;
            if (sample < 0)
            {
                sample = 0;
            }

            sumLatency += sample;
            count++;
        }

        if (count <= 0)
        {
            return;
        }

        var average = (int)(sumLatency / count);
        Volatile.Write(ref state.LastLatencyAvgMs, average);
        Volatile.Write(ref state.LastLatencyUpdatedMs, now);
    }

    public bool TryGetLastLatencyAverageMs(
        NodeId peerNodeId,
        int localSocketId,
        IPEndPoint remoteEndPoint,
        out int averageLatencyMs)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        var key = new ZeroTierPeerQosPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        if (_paths.TryGetValue(key, out var state) && Volatile.Read(ref state.LastLatencyUpdatedMs) != 0)
        {
            averageLatencyMs = Volatile.Read(ref state.LastLatencyAvgMs);
            return true;
        }

        averageLatencyMs = 0;
        return false;
    }

    private static bool ShouldTrack(ulong packetId)
        => (packetId & (ulong)(QosAckDivisor - 1)) != 0;

    private static void CleanupOutboundIfNeeded(PathState state, long now)
    {
        var last = Volatile.Read(ref state.LastOutboundCleanupMs);
        if (last != 0 && unchecked(now - last) < 1_000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref state.LastOutboundCleanupMs, now, last) != last)
        {
            return;
        }

        foreach (var pair in state.OutboundSentMs)
        {
            if (unchecked(now - pair.Value) >= DefaultRecordTimeoutMs)
            {
                state.OutboundSentMs.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class PathState
    {
        public ConcurrentQueue<PendingInboundRecord> Inbound { get; } = new();
        public int InboundCount;
        public long LastSentMs;

        public ConcurrentDictionary<ulong, long> OutboundSentMs { get; } = new();
        public long LastOutboundCleanupMs;
        public int LastLatencyAvgMs;
        public long LastLatencyUpdatedMs;
    }

    private readonly record struct PendingInboundRecord(ulong PacketId, long ReceivedMs);
}

internal readonly record struct ZeroTierPeerQosPathKey(NodeId PeerNodeId, ZeroTierPeerPhysicalPathKey Path);
