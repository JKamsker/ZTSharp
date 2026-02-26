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

    // See: ZeroTierOne node/Bond.cpp (qosStatsOut timeout = _qosSendInterval * 3).
    private const long DefaultRecordTimeoutMs = 30_000;

    private readonly Func<long> _nowMs;
    private readonly ConcurrentDictionary<ZeroTierPeerQosPathKey, PathState> _paths = new();

    public ZeroTierPeerQosManager(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
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
        public ConcurrentDictionary<ulong, long> OutboundSentMs { get; } = new();
        public long LastOutboundCleanupMs;
        public int LastLatencyAvgMs;
        public long LastLatencyUpdatedMs;
    }
}

internal readonly record struct ZeroTierPeerQosPathKey(NodeId PeerNodeId, ZeroTierPeerPhysicalPathKey Path);

