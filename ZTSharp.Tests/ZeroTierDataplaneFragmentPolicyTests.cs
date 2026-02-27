using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierDataplaneFragmentPolicyTests
{
    [Fact]
    public async Task Ipv4Fragments_AreDropped_BeforeUdpHandlerDispatch()
    {
        var localManagedIpV4 = IPAddress.Parse("10.0.0.2");
        await using var runtime = CreateRuntime(localManagedIpV4);
        var ip = GetIpHandler(runtime);

        const ushort localPort = 12010;
        var udpChannel = Channel.CreateUnbounded<ZeroTierRoutedIpPacket>();
        Assert.True(runtime.TryRegisterUdpPort(AddressFamily.InterNetwork, localPort, udpChannel.Writer));

        var remoteIp = IPAddress.Parse("10.0.0.1");
        var udp = UdpCodec.Encode(remoteIp, localManagedIpV4, sourcePort: 1111, destinationPort: localPort, payload: new byte[] { 1, 2, 3 });
        var ipv4 = Ipv4Codec.Encode(remoteIp, localManagedIpV4, UdpCodec.ProtocolNumber, udp, identification: 1);

        // Mark as fragmented (More Fragments flag) and update header checksum.
        var flagsAndOffset = BinaryPrimitives.ReadUInt16BigEndian(ipv4.AsSpan(6, 2));
        flagsAndOffset |= 0x2000;
        BinaryPrimitives.WriteUInt16BigEndian(ipv4.AsSpan(6, 2), flagsAndOffset);
        RewriteIpv4HeaderChecksum(ipv4);

        Assert.True(Ipv4Codec.IsFragmented(ipv4));

        await ip.HandleIpv4PacketAsync(peerNodeId: new NodeId(0x3333333333), ipv4Packet: ipv4, cancellationToken: CancellationToken.None);

        Assert.False(udpChannel.Reader.TryRead(out _));
    }

    [Fact]
    public void Ipv6ExtensionHeaders_AreRecognized()
    {
        Assert.True(Ipv6Codec.IsExtensionHeader(0)); // Hop-by-Hop
        Assert.True(Ipv6Codec.IsExtensionHeader(43)); // Routing
        Assert.True(Ipv6Codec.IsExtensionHeader(44)); // Fragment
        Assert.True(Ipv6Codec.IsExtensionHeader(60)); // Destination Options
        Assert.False(Ipv6Codec.IsExtensionHeader(UdpCodec.ProtocolNumber));
        Assert.False(Ipv6Codec.IsExtensionHeader(TcpCodec.ProtocolNumber));
    }

    private static void RewriteIpv4HeaderChecksum(byte[] packet)
    {
        var headerLength = (packet[0] & 0x0F) * 4;
        Assert.True(headerLength >= Ipv4Codec.MinimumHeaderLength);

        // zero checksum field
        packet[10] = 0;
        packet[11] = 0;

        var checksum = ComputeIpv4HeaderChecksum(packet.AsSpan(0, headerLength));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), checksum);
    }

    private static ushort ComputeIpv4HeaderChecksum(ReadOnlySpan<byte> header)
    {
        var sum = 0u;
        for (var i = 0; i < header.Length; i += 2)
        {
            var word = (i + 1 < header.Length)
                ? BinaryPrimitives.ReadUInt16BigEndian(header.Slice(i, 2))
                : (ushort)(header[i] << 8);
            sum += word;
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
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
