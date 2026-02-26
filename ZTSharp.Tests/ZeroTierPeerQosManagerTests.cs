using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierPeerQosManagerTests
{
    [Fact]
    public void HandleInboundMeasurement_MatchesIdsAndUpdatesLatencyAverage()
    {
        var now = 1_000L;
        var mgr = new ZeroTierPeerQosManager(nowMs: () => now);

        var peerNodeId = new NodeId(0x1111111111);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 9999);

        mgr.RecordOutgoingPacket(peerNodeId, localSocketId: 1, endpoint, packetId: 3);

        now = 1_500;
        Span<byte> payload = stackalloc byte[8 + 2];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(0, 8), 3UL);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(8, 2), 200);

        mgr.HandleInboundMeasurement(peerNodeId, localSocketId: 1, endpoint, payload);

        Assert.True(mgr.TryGetLastLatencyAverageMs(peerNodeId, localSocketId: 1, endpoint, out var avgMs));
        Assert.Equal(150, avgMs);
    }

    [Fact]
    public void RecordOutgoingPacket_SkipsUntrackedPacketIds()
    {
        var now = 1_000L;
        var mgr = new ZeroTierPeerQosManager(nowMs: () => now);

        var peerNodeId = new NodeId(0x1111111111);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 9999);

        mgr.RecordOutgoingPacket(peerNodeId, localSocketId: 1, endpoint, packetId: 4);

        now = 1_500;
        Span<byte> payload = stackalloc byte[8 + 2];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(0, 8), 4UL);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(8, 2), 200);

        mgr.HandleInboundMeasurement(peerNodeId, localSocketId: 1, endpoint, payload);

        Assert.False(mgr.TryGetLastLatencyAverageMs(peerNodeId, localSocketId: 1, endpoint, out _));
    }
}

