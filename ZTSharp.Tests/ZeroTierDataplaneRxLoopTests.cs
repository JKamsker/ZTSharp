using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierDataplaneRxLoopTests
{
    [Fact]
    public async Task UdpTransport_IncomingQueue_StopsAcceptingWrites_WhenFull()
    {
        await using var transport = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        var incoming = GetPrivateField<Channel<ZeroTierUdpDatagram>>(transport, "_incoming");

        var remote = new IPEndPoint(IPAddress.Loopback, 12345);

        const int capacity = 2048;
        const int writes = capacity + 512;

        var successfulWrites = 0;
        for (var i = 0; i < writes; i++)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(payload, i);
            if (incoming.Writer.TryWrite(new ZeroTierUdpDatagram(remote, payload)))
            {
                successfulWrites++;
            }
        }

        var firstId = -1;
        var lastId = -1;
        var count = 0;

        while (incoming.Reader.TryRead(out var datagram))
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(datagram.Payload);
            if (count == 0)
            {
                firstId = id;
            }

            lastId = id;
            count++;
        }

        Assert.Equal(capacity, successfulWrites);
        Assert.Equal(capacity, count);
        Assert.Equal(0, firstId);
        Assert.Equal(capacity - 1, lastId);
    }

    [Fact]
    public async Task UdpTransport_UnderModerateBurst_DoesNotDropReceivedDatagrams()
    {
        await using var receiver = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var sender = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        const int count = 512;
        for (var i = 0; i < count; i++)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(payload, i);
            await sender.SendAsync(receiver.LocalEndpoint, payload);
        }

        var received = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var datagram = await receiver.ReceiveAsync(TimeSpan.FromSeconds(2));
            received.Add(BinaryPrimitives.ReadInt32LittleEndian(datagram.Payload));
        }

        Assert.Equal(count, received.Count);
    }

    [Fact]
    public async Task DataplaneRuntime_PeerQueue_StopsAcceptingWrites_WhenFull()
    {
        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        var localIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);

        await using var runtime = new ZeroTierDataplaneRuntime(
            udp,
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localIdentity: localIdentity,
            networkId: 0x9ad07d01093a69e3UL,
            localManagedIpsV4: new[] { IPAddress.Parse("10.0.0.1") },
            localManagedIpsV6: Array.Empty<IPAddress>(),
            inlineCom: Array.Empty<byte>());

        var runtimeCts = GetPrivateField<CancellationTokenSource>(runtime, "_cts");
        runtimeCts.Cancel();

        var dispatcherLoop = GetPrivateField<Task>(runtime, "_dispatcherLoop");
        var peerLoop = GetPrivateField<Task>(runtime, "_peerLoop");
        await Task.WhenAll(dispatcherLoop, peerLoop).WaitAsync(TimeSpan.FromSeconds(2));

        var peerQueue = GetPrivateField<Channel<ZeroTierUdpDatagram>>(runtime, "_peerQueue");

        var remote = new IPEndPoint(IPAddress.Loopback, 12345);

        const int capacity = 2048;
        const int writes = capacity + 512;

        var successfulWrites = 0;
        for (var i = 0; i < writes; i++)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(payload, i);
            if (peerQueue.Writer.TryWrite(new ZeroTierUdpDatagram(remote, payload)))
            {
                successfulWrites++;
            }
        }

        var firstId = -1;
        var lastId = -1;
        var count = 0;

        while (peerQueue.Reader.TryRead(out var datagram))
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(datagram.Payload);
            if (count == 0)
            {
                firstId = id;
            }

            lastId = id;
            count++;
        }

        Assert.Equal(capacity, successfulWrites);
        Assert.Equal(capacity, count);
        Assert.Equal(0, firstId);
        Assert.Equal(capacity - 1, lastId);
    }

    [Fact]
    public async Task PeerLoopAsync_ContinuesAfterProcessorFault()
    {
        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        var rootClient = new ZeroTierDataplaneRootClient(
            udp,
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localNodeId: new NodeId(0x2222222222),
            networkId: 1,
            inlineCom: Array.Empty<byte>());

        var processor = new ThrowOncePeerDatagrams();
        var loops = new ZeroTierDataplaneRxLoops(
            udp,
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            localNodeId: new NodeId(0x2222222222),
            rootClient: rootClient,
            peerDatagrams: processor);

        var peerChannel = Channel.CreateUnbounded<ZeroTierUdpDatagram>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        using var cts = new CancellationTokenSource();
        var peerLoop = Task.Run(() => loops.PeerLoopAsync(peerChannel.Reader, cts.Token), CancellationToken.None);

        Assert.True(peerChannel.Writer.TryWrite(new ZeroTierUdpDatagram(new IPEndPoint(IPAddress.Loopback, 1), new byte[1])));
        Assert.True(peerChannel.Writer.TryWrite(new ZeroTierUdpDatagram(new IPEndPoint(IPAddress.Loopback, 2), new byte[1])));

        await processor.SecondCall.WaitAsync(TimeSpan.FromSeconds(2));

        cts.Cancel();
        await peerLoop.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DispatcherLoopAsync_ForwardsPeerDatagrams_FromAnyEndpoint()
    {
        await using var rxUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var rootSender = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var attackerSender = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var rootEndpoint = rootSender.LocalEndpoint;
        var rootNodeId = new NodeId(0x1111111111);
        var localNodeId = new NodeId(0x2222222222);

        var rootClient = new ZeroTierDataplaneRootClient(
            rxUdp,
            rootNodeId,
            rootEndpoint,
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localNodeId,
            networkId: 1,
            inlineCom: Array.Empty<byte>());

        var loops = new ZeroTierDataplaneRxLoops(
            rxUdp,
            rootNodeId,
            rootEndpoint,
            rootKey: new byte[48],
            localNodeId,
            rootClient,
            peerDatagrams: new NoopPeerDatagrams());

        var peerChannel = Channel.CreateUnbounded<ZeroTierUdpDatagram>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        using var cts = new CancellationTokenSource();
        var dispatcher = Task.Run(() => loops.DispatcherLoopAsync(peerChannel.Writer, cts.Token), CancellationToken.None);

        var peerNodeId = new NodeId(0x3333333333);
        var peerPacket = ZeroTierPacketCodec.Encode(
            new ZeroTierPacketHeader(
                PacketId: 1,
                Destination: localNodeId,
                Source: peerNodeId,
                Flags: 0,
                Mac: 0,
                VerbRaw: 0),
            ReadOnlySpan<byte>.Empty);

        await attackerSender.SendAsync(rxUdp.LocalEndpoint, peerPacket);
        await rootSender.SendAsync(rxUdp.LocalEndpoint, peerPacket);

        var forwarded1 = await ReadAsyncWithTimeout(peerChannel.Reader, TimeSpan.FromSeconds(2));
        var forwarded2 = await ReadAsyncWithTimeout(peerChannel.Reader, TimeSpan.FromSeconds(2));

        Assert.NotEqual(forwarded1.RemoteEndPoint, forwarded2.RemoteEndPoint);
        Assert.Contains(forwarded1.RemoteEndPoint, new[] { rootEndpoint, attackerSender.LocalEndpoint });
        Assert.Contains(forwarded2.RemoteEndPoint, new[] { rootEndpoint, attackerSender.LocalEndpoint });

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            _ = await ReadAsyncWithTimeout(peerChannel.Reader, TimeSpan.FromMilliseconds(100));
        });

        cts.Cancel();
        await dispatcher.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DispatcherLoopAsync_DropsPeerDatagrams_WhenPeerQueueIsFull_AndCountsDrops()
    {
        await using var rxUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var sender = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var rootClient = new ZeroTierDataplaneRootClient(
            rxUdp,
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localNodeId: new NodeId(0x2222222222),
            networkId: 1,
            inlineCom: Array.Empty<byte>());

        var drops = 0;
        var loops = new ZeroTierDataplaneRxLoops(
            rxUdp,
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            localNodeId: new NodeId(0x2222222222),
            rootClient: rootClient,
            peerDatagrams: new NoopPeerDatagrams(),
            onPeerQueueDrop: () => Interlocked.Increment(ref drops));

        var peerChannel = Channel.CreateBounded<ZeroTierUdpDatagram>(new BoundedChannelOptions(capacity: 1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        Assert.True(peerChannel.Writer.TryWrite(new ZeroTierUdpDatagram(new IPEndPoint(IPAddress.Loopback, 1), new byte[1])));

        using var cts = new CancellationTokenSource();
        var dispatcher = Task.Run(() => loops.DispatcherLoopAsync(peerChannel.Writer, cts.Token), CancellationToken.None);

        var localNodeId = new NodeId(0x2222222222);
        var peerNodeId = new NodeId(0x3333333333);
        var peerPacket = ZeroTierPacketCodec.Encode(
            new ZeroTierPacketHeader(
                PacketId: 1,
                Destination: localNodeId,
                Source: peerNodeId,
                Flags: 0,
                Mac: 0,
                VerbRaw: 0),
            ReadOnlySpan<byte>.Empty);

        await sender.SendAsync(rxUdp.LocalEndpoint, peerPacket);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref drops) >= 1, TimeSpan.FromSeconds(2)));

        cts.Cancel();
        await dispatcher.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ResolveNodeIdAsync_CachesResult_WhenRootResponseComesFromAlternateEndpoint()
    {
        var localIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);
        var rootIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x1111111111);

        await using var rootUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var rootReplyUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        var rootEndpoint = rootUdp.LocalEndpoint;

        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var rootKey = new byte[48];
        ZeroTierC25519.Agree(localIdentity.PrivateKey!, rootIdentity.PublicKey, rootKey);

        const ulong networkId = 0x9ad07d01093a69e3UL;
        var managedIp = IPAddress.Parse("10.121.15.99");
        var group = ZeroTierMulticastGroup.DeriveForAddressResolution(managedIp);
        var inlineCom = "inline-com-for-test"u8.ToArray();

        var rootClient = new ZeroTierDataplaneRootClient(
            udp,
            rootIdentity.NodeId,
            rootEndpoint,
            rootKey,
            rootProtocolVersion: 12,
            localIdentity.NodeId,
            networkId: networkId,
            inlineCom: inlineCom);

        var loops = new ZeroTierDataplaneRxLoops(
            udp,
            rootIdentity.NodeId,
            rootEndpoint,
            rootKey,
            localIdentity.NodeId,
            rootClient,
            peerDatagrams: new NoopPeerDatagrams());

        var peerChannel = Channel.CreateUnbounded<ZeroTierUdpDatagram>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        using var dispatcherCts = new CancellationTokenSource();
        var dispatcher = Task.Run(() => loops.DispatcherLoopAsync(peerChannel.Writer, dispatcherCts.Token), CancellationToken.None);

        var remoteNodeId = new NodeId(0xaaaaaaaaaa);
        var serverTask = RunGatherOkServerOnceAsync(
            rootUdp,
            rootReplyUdp,
            rootIdentity.NodeId,
            localIdentity.NodeId,
            rootKey,
            networkId,
            group,
            expectedInlineCom: inlineCom,
            members: new[] { remoteNodeId });

        var cache = new ManagedIpToNodeIdCache(
            capacity: 64,
            resolvedTtl: TimeSpan.FromHours(1),
            learnedTtl: TimeSpan.FromHours(1));

        var resolved1 = await rootClient.ResolveNodeIdAsync(managedIp, cache, CancellationToken.None);
        Assert.Equal(remoteNodeId, resolved1);

        await serverTask;

        var resolved2 = await rootClient.ResolveNodeIdAsync(managedIp, cache, CancellationToken.None);
        Assert.Equal(remoteNodeId, resolved2);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            _ = await rootUdp.ReceiveAsync(TimeSpan.FromMilliseconds(100));
        });

        dispatcherCts.Cancel();
        await dispatcher.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<T> ReadAsyncWithTimeout<T>(ChannelReader<T> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await reader.ReadAsync(cts.Token);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var value = field!.GetValue(instance);
        Assert.NotNull(value);
        return (T)value!;
    }

    private sealed class NoopPeerDatagrams : IZeroTierDataplanePeerDatagramProcessor
    {
        public Task ProcessAsync(ZeroTierUdpDatagram datagram, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowOncePeerDatagrams : IZeroTierDataplanePeerDatagramProcessor
    {
        private readonly TaskCompletionSource _secondCallTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task SecondCall => _secondCallTcs.Task;

        public Task ProcessAsync(ZeroTierUdpDatagram datagram, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                throw new InvalidOperationException("simulated processor fault");
            }

            _secondCallTcs.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static async Task RunGatherOkServerOnceAsync(
        ZeroTierUdpTransport receiveTransport,
        ZeroTierUdpTransport sendTransport,
        NodeId rootNodeId,
        NodeId localNodeId,
        byte[] rootKey,
        ulong networkId,
        ZeroTierMulticastGroup group,
        byte[] expectedInlineCom,
        NodeId[] members)
    {
        var datagram = await receiveTransport.ReceiveAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var packetBytes = datagram.Payload;

        Assert.True(ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded));
        Assert.Equal(rootNodeId, decoded.Header.Destination);
        Assert.Equal(localNodeId, decoded.Header.Source);

        Assert.True(ZeroTierPacketCrypto.Dearmor(packetBytes, rootKey));

        var verb = (ZeroTierVerb)(packetBytes[ZeroTierPacketHeader.IndexVerb] & 0x1F);
        Assert.Equal(ZeroTierVerb.MulticastGather, verb);

        var payload = packetBytes.AsSpan(ZeroTierPacketHeader.IndexPayload);
        Assert.True(payload.Length >= 23);
        Assert.Equal(networkId, BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(0, 8)));

        var flags = payload[8];
        Assert.True((flags & 0x01) != 0);
        Assert.True(payload.Length >= 23 + expectedInlineCom.Length);
        Assert.True(payload.Slice(23, expectedInlineCom.Length).SequenceEqual(expectedInlineCom));

        var requestPacketId = decoded.Header.PacketId;

        var totalKnown = (uint)members.Length;
        var added = (ushort)members.Length;

        var okVerbHeaderLength = 1 + 8;
        var okPayloadLength = okVerbHeaderLength + 8 + 6 + 4 + 4 + 2 + (members.Length * 5);
        var okPayload = new byte[okPayloadLength];

        okPayload[0] = (byte)ZeroTierVerb.MulticastGather;
        BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(1, 8), requestPacketId);

        var ptr = okVerbHeaderLength;
        BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(ptr, 8), networkId);
        ptr += 8;

        group.Mac.CopyTo(okPayload.AsSpan(ptr, 6));
        ptr += 6;

        BinaryPrimitives.WriteUInt32BigEndian(okPayload.AsSpan(ptr, 4), group.Adi);
        ptr += 4;

        BinaryPrimitives.WriteUInt32BigEndian(okPayload.AsSpan(ptr, 4), totalKnown);
        ptr += 4;

        BinaryPrimitives.WriteUInt16BigEndian(okPayload.AsSpan(ptr, 2), added);
        ptr += 2;

        foreach (var member in members)
        {
            var value = member.Value;
            okPayload[ptr++] = (byte)((value >> 32) & 0xFF);
            okPayload[ptr++] = (byte)((value >> 24) & 0xFF);
            okPayload[ptr++] = (byte)((value >> 16) & 0xFF);
            okPayload[ptr++] = (byte)((value >> 8) & 0xFF);
            okPayload[ptr++] = (byte)(value & 0xFF);
        }

        Assert.Equal(okPayloadLength, ptr);

        var okHeader = new ZeroTierPacketHeader(
            PacketId: 2,
            Destination: localNodeId,
            Source: rootNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Ok);

        var okPacket = ZeroTierPacketCodec.Encode(okHeader, okPayload);
        ZeroTierPacketCrypto.Armor(okPacket, rootKey, encryptPayload: true);

        await sendTransport.SendAsync(datagram.RemoteEndPoint, okPacket).ConfigureAwait(false);
    }
}
