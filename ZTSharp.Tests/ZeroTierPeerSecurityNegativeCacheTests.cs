using System.Net;
using System.Reflection;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierPeerSecurityNegativeCacheTests
{
    [Fact]
    public async Task NegativePeerKeyCache_DoesNotOverrideNonExpiredPositiveKey()
    {
        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        var localIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);

        var rootClient = new ZeroTierDataplaneRootClient(
            udp,
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localNodeId: localIdentity.NodeId,
            networkId: 1,
            inlineCom: Array.Empty<byte>());

        var peerSecurity = new ZeroTierDataplanePeerSecurity(udp, rootClient, localIdentity);

        var peerNodeId = new NodeId(0x3333333333);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = new byte[48];
        key[0] = 0x42;

        InvokePrivate(peerSecurity, "CachePeerKey", peerNodeId, key, nowMs);
        InvokePrivate(peerSecurity, "CacheNegativePeerKey", peerNodeId, nowMs + 1);

        Assert.True(peerSecurity.TryGetPeerKey(peerNodeId, out var cached));
        Assert.Same(key, cached);
    }

    [Fact]
    public async Task ShutdownCancellation_DoesNotNegativeCachePeerKey()
    {
        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        var localIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);

        var rootClient = new ZeroTierDataplaneRootClient(
            udp,
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localNodeId: localIdentity.NodeId,
            networkId: 1,
            inlineCom: Array.Empty<byte>());

        var peerSecurity = new ZeroTierDataplanePeerSecurity(udp, rootClient, localIdentity);

        var peerNodeId = new NodeId(0x3333333333);
        var cts = GetPrivateField<CancellationTokenSource>(peerSecurity, "_cts");
        cts.Cancel();

        var task = (Task<byte[]>)InvokePrivate(peerSecurity, "FetchAndCachePeerKeyAsync", peerNodeId);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);

        var peerKeys = GetPrivateField<object>(peerSecurity, "_peerKeys");
        Assert.False(ContainsKey(peerKeys, peerNodeId));
    }

    private static bool ContainsKey(object concurrentDictionary, NodeId key)
    {
        var method = concurrentDictionary.GetType().GetMethod("ContainsKey", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        return (bool)method!.Invoke(concurrentDictionary, new object[] { key })!;
    }

    private static object InvokePrivate(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!.Invoke(instance, args)!;
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var value = field!.GetValue(instance);
        Assert.NotNull(value);
        return (T)value!;
    }
}
