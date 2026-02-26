using System.Net;
using System.Reflection;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierIpv4LinkSenderTests
{
    [Fact]
    public async Task SendIpv4Async_SendsToDirectEndpoints_AndRelay()
    {
        await using var txUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var directRx = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var relayRx = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var remoteNodeId = new NodeId(0x3333333333);
        var relayEndpoint = TestUdpEndpoints.ToLoopback(relayRx.LocalEndpoint);
        var directEndpoint = TestUdpEndpoints.ToLoopback(directRx.LocalEndpoint);
        var directManager = new ZeroTierDirectEndpointManager(txUdp, relayEndpoint, remoteNodeId);
        SetPrivateField(directManager, "_directEndpoints", new[] { directEndpoint });

        var localNodeId = new NodeId(0x2222222222);
        const ulong networkId = 1;
        var inlineCom = Array.Empty<byte>();
        var to = ZeroTierMac.FromAddress(remoteNodeId, networkId);
        var from = ZeroTierMac.FromAddress(localNodeId, networkId);
        var sharedKey = new byte[48];

        var sender = new ZeroTierIpv4LinkSender(
            txUdp,
            relayEndpoint: relayEndpoint,
            directEndpoints: directManager,
            localNodeId: localNodeId,
            remoteNodeId: remoteNodeId,
            networkId: networkId,
            inlineCom: inlineCom,
            to: to,
            from: from,
            sharedKey: sharedKey,
            remoteProtocolVersion: 12);

        var ipv4Packet = new byte[20];
        await sender.SendIpv4Async(ipv4Packet, CancellationToken.None);

        _ = await directRx.ReceiveAsync(TimeSpan.FromSeconds(2));
        _ = await relayRx.ReceiveAsync(TimeSpan.FromSeconds(2));
    }

    private static void SetPrivateField<T>(object instance, string fieldName, T value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }
}
