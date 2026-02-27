using System.Net;
using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierUdpSocketReceiveTests
{
    [Fact]
    public async Task ReceiveFromAsync_WildcardSemantics_AllowAnyLocalManagedIp()
    {
        var localIpA = IPAddress.Parse("10.0.0.2");
        var localIpB = IPAddress.Parse("10.0.0.3");
        await using var runtime = CreateRuntime(localIpA, localIpB);
        var ip = GetIpHandler(runtime);

        const ushort localPort = 12010;
        await using var socket = new ZeroTierUdpSocket(runtime, localIpA, localPort);

        var buffer = new byte[32];
        var receiveTask = socket.ReceiveFromAsync(buffer, TimeSpan.FromSeconds(1)).AsTask();

        var remoteIp = IPAddress.Parse("10.0.0.1");
        var payload = new byte[] { 1, 2, 3, 4 };
        var udp = UdpCodec.Encode(remoteIp, localIpB, sourcePort: 1111, destinationPort: localPort, payload);
        var ipv4 = Ipv4Codec.Encode(remoteIp, localIpB, protocol: UdpCodec.ProtocolNumber, payload: udp, identification: 1);

        await ip.HandleIpv4PacketAsync(peerNodeId: new NodeId(0x3333333333), ipv4Packet: ipv4, cancellationToken: CancellationToken.None);

        var received = await receiveTask;
        Assert.Equal(payload.Length, received.ReceivedBytes);
        Assert.Equal(new IPEndPoint(remoteIp, 1111), received.RemoteEndPoint);
        Assert.Equal(payload, buffer.AsSpan(0, payload.Length).ToArray());
    }

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
    private static ZeroTierDataplaneRuntime CreateRuntime(IPAddress localManagedIpV4A, IPAddress localManagedIpV4B)
        => new(
            udp: new ZeroTierUdpTransport(localPort: 0, enableIpv6: false),
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localIdentity: ZeroTierTestIdentities.CreateFastIdentity(0x2222222222),
            networkId: 1,
            localManagedIpsV4: new[] { localManagedIpV4A, localManagedIpV4B },
            localManagedIpsV6: Array.Empty<IPAddress>(),
            inlineCom: new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
}

