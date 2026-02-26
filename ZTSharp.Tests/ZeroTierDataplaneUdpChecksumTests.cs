using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierDataplaneUdpChecksumTests
{
    [Fact]
    public async Task InvalidUdpChecksum_IsDropped_BeforeUdpHandlerDispatch()
    {
        var localManagedIpV4 = IPAddress.Parse("10.0.0.2");
        await using var runtime = CreateRuntime(localManagedIpV4);
        var ip = GetIpHandler(runtime);

        const ushort localPort = 12000;
        var udpChannel = Channel.CreateUnbounded<ZeroTierRoutedIpPacket>();
        Assert.True(runtime.TryRegisterUdpPort(AddressFamily.InterNetwork, localPort, udpChannel.Writer));

        var remoteIp = IPAddress.Parse("10.0.0.1");
        var udp = UdpCodec.Encode(remoteIp, localManagedIpV4, sourcePort: 1111, destinationPort: localPort, payload: new byte[] { 1, 2, 3 });
        udp[6] ^= 0x01; // checksum

        var ipv4 = Ipv4Codec.Encode(remoteIp, localManagedIpV4, UdpCodec.ProtocolNumber, udp, identification: 1);
        await ip.HandleIpv4PacketAsync(peerNodeId: new NodeId(0x3333333333), ipv4Packet: ipv4, cancellationToken: CancellationToken.None);

        Assert.False(udpChannel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ValidUdpChecksum_IsDelivered_ToUdpHandler()
    {
        var localManagedIpV4 = IPAddress.Parse("10.0.0.2");
        await using var runtime = CreateRuntime(localManagedIpV4);
        var ip = GetIpHandler(runtime);

        const ushort localPort = 12001;
        var udpChannel = Channel.CreateUnbounded<ZeroTierRoutedIpPacket>();
        Assert.True(runtime.TryRegisterUdpPort(AddressFamily.InterNetwork, localPort, udpChannel.Writer));

        var remoteIp = IPAddress.Parse("10.0.0.1");
        var udp = UdpCodec.Encode(remoteIp, localManagedIpV4, sourcePort: 1111, destinationPort: localPort, payload: new byte[] { 1, 2, 3 });
        var ipv4 = Ipv4Codec.Encode(remoteIp, localManagedIpV4, UdpCodec.ProtocolNumber, udp, identification: 1);

        await ip.HandleIpv4PacketAsync(peerNodeId: new NodeId(0x3333333333), ipv4Packet: ipv4, cancellationToken: CancellationToken.None);

        Assert.True(udpChannel.Reader.TryRead(out var delivered));
        Assert.Equal(new NodeId(0x3333333333), delivered.PeerNodeId);
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
