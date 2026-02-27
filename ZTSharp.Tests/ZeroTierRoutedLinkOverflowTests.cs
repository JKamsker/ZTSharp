using System.IO;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierRoutedLinkOverflowTests
{
    [Fact]
    public async Task RouteBacklogOverflow_FailsWithIOException_InsteadOfSilentDrop()
    {
        var localManagedIpV4 = IPAddress.Parse("10.0.0.2");
        await using var runtime = CreateRuntime(localManagedIpV4);

        var local = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 54321);
        var routeKey = ZeroTierTcpRouteKey.FromEndpoints(local, remote);
        var peer = new NodeId(0x3333333333);
        var route = new ZeroTierRoutedIpv4Link(runtime, routeKey, peer);

        var payload = new byte[60];

        var wrote = 0;
        while (route.TryEnqueueIncoming(payload))
        {
            wrote++;
            if (wrote > 10_000)
            {
                Assert.Fail("Expected the route backlog to become full.");
            }
        }

        Assert.True(route.IncomingDropCount >= 1);

        await Assert.ThrowsAsync<IOException>(async () => await route.ReceiveAsync());
        await Assert.ThrowsAsync<IOException>(async () => await route.SendAsync(payload));
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
