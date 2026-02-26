using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierIcmpv6ChecksumTests
{
    [Fact]
    public async Task NeighborSolicitation_InvalidChecksum_IsDroppedWithoutSending()
    {
        var localV4 = IPAddress.Parse("10.0.0.2");
        var localV6 = IPAddress.Parse("fd00::2");

        await using var runtime = CreateRuntime(localV4, localManagedIpsV6: new[] { localV6 });
        var localMac = ZeroTierMac.FromAddress(runtime.NodeId, networkId: 1);
        var handler = new ZeroTierDataplaneIcmpv6Handler(runtime, localMac, new[] { localV6 });

        var sourceIp = IPAddress.Parse("fd00::1");
        var destinationIp = IPAddress.Parse("ff02::1:ff00:2");
        var ns = BuildNeighborSolicitation(sourceIp, destinationIp, targetIp: localV6);

        ns[2] ^= 0x01; // checksum

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await handler.HandleAsync(peerNodeId: new NodeId(0x3333333333), sourceIp, destinationIp, hopLimit: 255, icmpMessage: ns, cancellationToken: cts.Token);
    }

    [Fact]
    public async Task NeighborSolicitation_ValidChecksum_AttemptsToSend_WhenNotDropped()
    {
        var localV4 = IPAddress.Parse("10.0.0.2");
        var localV6 = IPAddress.Parse("fd00::2");

        await using var runtime = CreateRuntime(localV4, localManagedIpsV6: new[] { localV6 });
        var localMac = ZeroTierMac.FromAddress(runtime.NodeId, networkId: 1);
        var handler = new ZeroTierDataplaneIcmpv6Handler(runtime, localMac, new[] { localV6 });

        var sourceIp = IPAddress.Parse("fd00::1");
        var destinationIp = IPAddress.Parse("ff02::1:ff00:2");
        var ns = BuildNeighborSolicitation(sourceIp, destinationIp, targetIp: localV6);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.HandleAsync(peerNodeId: new NodeId(0x3333333333), sourceIp, destinationIp, hopLimit: 255, icmpMessage: ns, cancellationToken: cts.Token).AsTask());
    }

    private static byte[] BuildNeighborSolicitation(IPAddress sourceIp, IPAddress destinationIp, IPAddress targetIp)
    {
        var ns = new byte[24];
        ns[0] = 135; // NS
        ns[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(ns.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(ns.AsSpan(4, 4), 0);
        targetIp.GetAddressBytes().CopyTo(ns.AsSpan(8, 16));

        var checksum = Icmpv6Codec.ComputeChecksum(sourceIp, destinationIp, ns);
        BinaryPrimitives.WriteUInt16BigEndian(ns.AsSpan(2, 2), checksum);
        return ns;
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "UDP transport ownership transfers to ZeroTierDataplaneRuntime, which is disposed by the caller.")]
    private static ZeroTierDataplaneRuntime CreateRuntime(IPAddress localManagedIpV4, IReadOnlyList<IPAddress> localManagedIpsV6)
        => new(
            udp: new ZeroTierUdpTransport(localPort: 0, enableIpv6: false),
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localIdentity: ZeroTierTestIdentities.CreateFastIdentity(0x2222222222),
            networkId: 1,
            localManagedIpV4: localManagedIpV4,
            localManagedIpsV6: localManagedIpsV6,
            inlineCom: new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
}
