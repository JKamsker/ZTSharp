using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierPeerEchoManagerTests
{
    [Fact]
    public async Task EchoProbe_UsesOnWirePacketId_AndUpdatesRttOnOk()
    {
        var udp = new RecordingUdpTransport();

        var now = 1_000L;
        var localNodeId = new NodeId(0x2222222222);
        var peerNodeId = new NodeId(0x1111111111);
        var mgr = new ZeroTierPeerEchoManager(udp, localNodeId, getPeerProtocolVersion: _ => 12, nowUnixMs: () => now);

        var endpoint = new IPEndPoint(IPAddress.Loopback, 5555);
        var sharedKey = Enumerable.Repeat((byte)7, 48).ToArray();

        await mgr.TrySendEchoProbeAsync(peerNodeId, localSocketId: 1, endpoint, sharedKey, CancellationToken.None);

        Assert.Single(udp.Sends);
        Assert.Equal(1, udp.Sends[0].LocalSocketId);
        Assert.Equal(endpoint, udp.Sends[0].RemoteEndPoint);

        var onWireEchoPacketId = BinaryPrimitives.ReadUInt64BigEndian(udp.Sends[0].Payload.Span.Slice(0, 8));

        now += 25;
        Span<byte> okTail = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(okTail, 1_000UL);
        mgr.HandleEchoOk(peerNodeId, localSocketId: 1, endpoint, onWireEchoPacketId, okTail);

        Assert.True(mgr.TryGetLastRttMs(peerNodeId, localSocketId: 1, endpoint, out var rttMs));
        Assert.Equal(25, rttMs);

        await mgr.TrySendEchoProbeAsync(peerNodeId, localSocketId: 1, endpoint, sharedKey, CancellationToken.None);
        Assert.Single(udp.Sends);
    }

    [Fact]
    public async Task HandleEchoRequest_SendsOkEchoWithTimestampEcho()
    {
        var udp = new RecordingUdpTransport();
        var localNodeId = new NodeId(0x2222222222);
        var peerNodeId = new NodeId(0x1111111111);
        var mgr = new ZeroTierPeerEchoManager(udp, localNodeId, getPeerProtocolVersion: _ => 12, nowUnixMs: () => 1_000);

        var endpoint = new IPEndPoint(IPAddress.Loopback, 5555);
        var sharedKey = Enumerable.Repeat((byte)7, 48).ToArray();

        var echoPayload = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(echoPayload, 777UL);

        await mgr.HandleEchoRequestAsync(
            peerNodeId,
            localSocketId: 2,
            endpoint,
            inRePacketId: 123,
            echoPayload,
            sharedKey,
            CancellationToken.None);

        Assert.Single(udp.Sends);
        Assert.Equal(2, udp.Sends[0].LocalSocketId);

        var decrypted = udp.Sends[0].Payload.ToArray();
        Assert.True(ZeroTierPacketCrypto.Dearmor(decrypted, sharedKey));

        var verb = (ZeroTierVerb)(decrypted[ZeroTierPacketHeader.IndexVerb] & 0x1F);
        Assert.Equal(ZeroTierVerb.Ok, verb);

        var payload = decrypted.AsSpan(ZeroTierPacketHeader.IndexPayload);
        Assert.True(payload.Length >= 1 + 8 + 8);
        Assert.Equal(ZeroTierVerb.Echo, (ZeroTierVerb)(payload[0] & 0x1F));
        Assert.Equal(123UL, BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1, 8)));
        Assert.Equal(777UL, BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1 + 8, 8)));
    }

    private sealed class RecordingUdpTransport : IZeroTierUdpTransport
    {
        public List<SendCall> Sends { get; } = new();

        public IReadOnlyList<ZeroTierUdpLocalSocket> LocalSockets { get; } =
            new[] { new ZeroTierUdpLocalSocket(Id: 0, LocalEndpoint: new IPEndPoint(IPAddress.Loopback, 0)) };

        public ValueTask<ZeroTierUdpDatagram> ReceiveAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ZeroTierUdpDatagram> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SendAsync(IPEndPoint remoteEndpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => SendAsync(localSocketId: 0, remoteEndpoint, payload, cancellationToken);

        public Task SendAsync(int localSocketId, IPEndPoint remoteEndpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Sends.Add(new SendCall(localSocketId, remoteEndpoint, payload));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly record struct SendCall(int LocalSocketId, IPEndPoint RemoteEndPoint, ReadOnlyMemory<byte> Payload);
}
