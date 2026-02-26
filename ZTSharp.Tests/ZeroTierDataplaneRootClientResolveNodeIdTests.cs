using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierDataplaneRootClientResolveNodeIdTests
{
    [Fact]
    public async Task ResolveNodeIdAsync_MultipleGatherMembers_DoesNotCacheResult()
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

        var cache = new ManagedIpToNodeIdCache(
            capacity: 64,
            resolvedTtl: TimeSpan.FromHours(1),
            learnedTtl: TimeSpan.FromHours(1));

        var managedIp = IPAddress.Parse("10.0.0.123");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var resolveTask = rootClient.ResolveNodeIdAsync(managedIp, cache, cts.Token);

        var pendingGather = GetPrivateField<ConcurrentDictionary<ulong, TaskCompletionSource<(uint TotalKnown, NodeId[] Members)>>>(rootClient, "_pendingGather");
        Assert.True(SpinWait.SpinUntil(() => pendingGather.Count == 1, TimeSpan.FromSeconds(2)));

        var packetId = pendingGather.Keys.Single();
        var members = new[] { new NodeId(0xaaaaaaaaaa), new NodeId(0xbbbbbbbbbb) };
        var responsePayload = EncodeGatherOkResponsePayload(inRePacketId: packetId, networkId: 1, group: ZeroTierMulticastGroup.DeriveForAddressResolution(managedIp), totalKnown: 2, members);

        Assert.True(rootClient.TryDispatchResponse(ZeroTierVerb.Ok, responsePayload));
        var resolved = await resolveTask;

        Assert.Equal(members[0], resolved);
        Assert.False(cache.TryGet(managedIp, out _));
    }

    [Fact]
    public async Task ResolveNodeIdAsync_SingleGatherMember_CachesResult()
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

        var cache = new ManagedIpToNodeIdCache(
            capacity: 64,
            resolvedTtl: TimeSpan.FromHours(1),
            learnedTtl: TimeSpan.FromHours(1));

        var managedIp = IPAddress.Parse("10.0.0.123");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var resolveTask = rootClient.ResolveNodeIdAsync(managedIp, cache, cts.Token);

        var pendingGather = GetPrivateField<ConcurrentDictionary<ulong, TaskCompletionSource<(uint TotalKnown, NodeId[] Members)>>>(rootClient, "_pendingGather");
        Assert.True(SpinWait.SpinUntil(() => pendingGather.Count == 1, TimeSpan.FromSeconds(2)));

        var packetId = pendingGather.Keys.Single();
        var members = new[] { new NodeId(0xaaaaaaaaaa) };
        var responsePayload = EncodeGatherOkResponsePayload(inRePacketId: packetId, networkId: 1, group: ZeroTierMulticastGroup.DeriveForAddressResolution(managedIp), totalKnown: 1, members);

        Assert.True(rootClient.TryDispatchResponse(ZeroTierVerb.Ok, responsePayload));
        var resolved = await resolveTask;

        Assert.Equal(members[0], resolved);
        Assert.True(cache.TryGet(managedIp, out var cached));
        Assert.Equal(members[0], cached);
    }

    private static byte[] EncodeGatherOkResponsePayload(
        ulong inRePacketId,
        ulong networkId,
        in ZeroTierMulticastGroup group,
        uint totalKnown,
        NodeId[] members)
    {
        var ok = new byte[8 + 6 + 4 + 4 + 2 + (members.Length * 5)];
        var okSpan = ok.AsSpan();
        BinaryPrimitives.WriteUInt64BigEndian(okSpan.Slice(0, 8), networkId);
        group.Mac.CopyTo(okSpan.Slice(8, 6));
        BinaryPrimitives.WriteUInt32BigEndian(okSpan.Slice(14, 4), group.Adi);
        BinaryPrimitives.WriteUInt32BigEndian(okSpan.Slice(18, 4), totalKnown);
        BinaryPrimitives.WriteUInt16BigEndian(okSpan.Slice(22, 2), checked((ushort)members.Length));

        var p = 24;
        foreach (var member in members)
        {
            ZeroTierBinaryPrimitives.WriteUInt40BigEndian(okSpan.Slice(p, 5), member.Value);
            p += 5;
        }

        var payload = new byte[1 + 8 + ok.Length];
        payload[0] = (byte)ZeroTierVerb.MulticastGather;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1, 8), inRePacketId);
        ok.CopyTo(payload.AsSpan(1 + 8));
        return payload;
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var value = field!.GetValue(instance);
        Assert.NotNull(value);
        return (T)value!;
    }
}
