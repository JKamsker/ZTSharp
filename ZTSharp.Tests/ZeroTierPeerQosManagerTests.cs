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

    [Fact]
    public void TryBuildOutboundPayload_EncodesLittleEndianPacketIdAndHoldingTime()
    {
        var now = 1_000L;
        var mgr = new ZeroTierPeerQosManager(nowMs: () => now);

        var peerNodeId = new NodeId(0x1111111111);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 9999);

        mgr.RecordIncomingPacket(peerNodeId, localSocketId: 1, endpoint, packetId: 3);

        now = 1_200;
        Assert.True(mgr.TryBuildOutboundPayload(peerNodeId, localSocketId: 1, endpoint, out var payload));
        Assert.Equal(10, payload.Length);

        var span = payload.AsSpan();
        Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(0, 8)));
        Assert.Equal((ushort)200, BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8, 2)));

        Assert.False(mgr.TryBuildOutboundPayload(peerNodeId, localSocketId: 1, endpoint, out _));
    }

    [Fact]
    public void TryGetLastLatencyAverageMs_ReturnsFalse_WhenAverageIsStale()
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

        now = 1_500 + 120_000 + 1;
        Assert.False(mgr.TryGetLastLatencyAverageMs(peerNodeId, localSocketId: 1, endpoint, out _));
    }
}
