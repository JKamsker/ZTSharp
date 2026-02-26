using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ManagedIpToNodeIdCacheTests
{
    [Fact]
    public void LearnFromNeighbor_IsCapacityBounded()
    {
        var cache = new ManagedIpToNodeIdCache(
            capacity: 8,
            resolvedTtl: TimeSpan.FromHours(1),
            learnedTtl: TimeSpan.FromHours(1));

        for (var i = 0; i < 100; i++)
        {
            var ip = new IPAddress(new byte[] { 10, 0, (byte)(i / 256), (byte)(i % 256) });
            cache.LearnFromNeighbor(ip, new NodeId((ulong)i + 1));
        }

        Assert.True(cache.Count <= 8);
    }

    [Fact]
    public void LearnFromNeighbor_DoesNotOverrideResolvedEntry()
    {
        var cache = new ManagedIpToNodeIdCache(
            capacity: 64,
            resolvedTtl: TimeSpan.FromHours(1),
            learnedTtl: TimeSpan.FromHours(1));

        var ip = IPAddress.Parse("10.0.0.123");
        cache.SetResolved(ip, new NodeId(0x1111111111));
        cache.LearnFromNeighbor(ip, new NodeId(0x2222222222));

        Assert.True(cache.TryGet(ip, out var nodeId));
        Assert.Equal(new NodeId(0x1111111111), nodeId);
    }

    [Fact]
    public async Task SpoofedIpv4SourceIp_DoesNotPopulateIpToNodeIdCache()
    {
        var localManagedIpV4 = IPAddress.Parse("10.0.0.2");
        await using var runtime = CreateRuntime(localManagedIpV4);
        var ipHandler = GetIpHandler(runtime);
        var cache = GetManagedIpCache(runtime);

        for (var i = 0; i < 64; i++)
        {
            var spoofedSrc = new IPAddress(new byte[] { 10, 0, 1, (byte)i });
            var udp = UdpCodec.Encode(spoofedSrc, localManagedIpV4, sourcePort: 20000, destinationPort: 20001, payload: new byte[] { 1 });
            var ipv4 = Ipv4Codec.Encode(spoofedSrc, localManagedIpV4, UdpCodec.ProtocolNumber, udp, identification: (ushort)(i + 1));

            await ipHandler.HandleIpv4PacketAsync(peerNodeId: new NodeId(0x3333333333), ipv4Packet: ipv4, cancellationToken: CancellationToken.None);
        }

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task SpoofedIpv4SourceIp_DoesNotOverrideResolvedCacheEntry()
    {
        var localManagedIpV4 = IPAddress.Parse("10.0.0.2");
        await using var runtime = CreateRuntime(localManagedIpV4);
        var ipHandler = GetIpHandler(runtime);
        var cache = GetManagedIpCache(runtime);

        var spoofedSrc = IPAddress.Parse("10.0.0.123");
        cache.SetResolved(spoofedSrc, new NodeId(0x1111111111));

        var udp = UdpCodec.Encode(spoofedSrc, localManagedIpV4, sourcePort: 20000, destinationPort: 20001, payload: new byte[] { 1 });
        var ipv4 = Ipv4Codec.Encode(spoofedSrc, localManagedIpV4, UdpCodec.ProtocolNumber, udp, identification: 1);
        await ipHandler.HandleIpv4PacketAsync(peerNodeId: new NodeId(0x3333333333), ipv4Packet: ipv4, cancellationToken: CancellationToken.None);

        Assert.True(cache.TryGet(spoofedSrc, out var nodeId));
        Assert.Equal(new NodeId(0x1111111111), nodeId);
    }

    private static ManagedIpToNodeIdCache GetManagedIpCache(ZeroTierDataplaneRuntime runtime)
        => GetPrivateField<ManagedIpToNodeIdCache>(runtime, "_managedIpToNodeId");

    private static ZeroTierDataplaneIpHandler GetIpHandler(ZeroTierDataplaneRuntime runtime)
    {
        var peerPackets = GetPrivateField<ZeroTierDataplanePeerPacketHandler>(runtime, "_peerPackets");
        return GetPrivateField<ZeroTierDataplaneIpHandler>(peerPackets, "_ip");
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var value = field!.GetValue(instance);
        Assert.NotNull(value);
        return (T)value!;
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "UDP transport ownership transfers to ZeroTierDataplaneRuntime, which is disposed by the caller.")]
    private static ZeroTierDataplaneRuntime CreateRuntime(IPAddress localManagedIpV4)
        => new(
            udp: new ZeroTierUdpTransport(localPort: 0, enableIpv6: false),
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localIdentity: ZeroTierTestIdentities.CreateFastIdentity(0x2222222222),
            networkId: 1,
            localManagedIpsV4: new[] { localManagedIpV4 },
            localManagedIpsV6: Array.Empty<IPAddress>(),
            inlineCom: new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
}
