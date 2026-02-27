using System.Net;
using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierUdpSocketDisposeTests
{
    [Fact]
    public async Task DisposeAsync_IsIdempotent_UnderConcurrency()
    {
        await using var runtime = CreateRuntime(IPAddress.Parse("10.0.0.2"));
        await using var socket = new ZeroTierUdpSocket(runtime, IPAddress.Parse("10.0.0.2"), localPort: 23470);

        await Task.WhenAll(
            socket.DisposeAsync().AsTask(),
            socket.DisposeAsync().AsTask(),
            socket.DisposeAsync().AsTask());
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

