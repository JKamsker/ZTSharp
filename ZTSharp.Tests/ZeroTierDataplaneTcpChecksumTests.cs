using System.Net;
using System.Net.Sockets;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierDataplaneTcpChecksumTests
{
    [Fact]
    public async Task InvalidTcpChecksumSyn_DoesNotTriggerSynHandler()
    {
        var localManagedIpV4 = IPAddress.Parse("10.0.0.2");
        await using var runtime = CreateRuntime(localManagedIpV4);
        var ip = GetIpHandler(runtime);

        const ushort localPort = 23461;
        var called = 0;
        Assert.True(runtime.TryRegisterTcpListener(AddressFamily.InterNetwork, localPort, onSyn: (_, _, _) =>
        {
            Interlocked.Increment(ref called);
            return Task.CompletedTask;
        }));

        var remoteIp = IPAddress.Parse("10.0.0.1");
        var tcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localManagedIpV4,
            sourcePort: 50000,
            destinationPort: localPort,
            sequenceNumber: 123,
            acknowledgmentNumber: 0,
            flags: TcpCodec.Flags.Syn,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);
        tcp[16] ^= 0x01; // checksum

        var ipv4 = Ipv4Codec.Encode(remoteIp, localManagedIpV4, TcpCodec.ProtocolNumber, tcp, identification: 1);
        await ip.HandleIpv4PacketAsync(peerNodeId: new NodeId(0x3333333333), ipv4Packet: ipv4, cancellationToken: CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref called));
    }

    [Fact]
    public async Task ValidTcpChecksumSyn_TriggersSynHandler()
    {
        var localManagedIpV4 = IPAddress.Parse("10.0.0.2");
        await using var runtime = CreateRuntime(localManagedIpV4);
        var ip = GetIpHandler(runtime);

        const ushort localPort = 23462;
        var called = 0;
        Assert.True(runtime.TryRegisterTcpListener(AddressFamily.InterNetwork, localPort, onSyn: (_, _, _) =>
        {
            Interlocked.Increment(ref called);
            return Task.CompletedTask;
        }));

        var remoteIp = IPAddress.Parse("10.0.0.1");
        var tcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localManagedIpV4,
            sourcePort: 50000,
            destinationPort: localPort,
            sequenceNumber: 123,
            acknowledgmentNumber: 0,
            flags: TcpCodec.Flags.Syn,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        var ipv4 = Ipv4Codec.Encode(remoteIp, localManagedIpV4, TcpCodec.ProtocolNumber, tcp, identification: 1);
        await ip.HandleIpv4PacketAsync(peerNodeId: new NodeId(0x3333333333), ipv4Packet: ipv4, cancellationToken: CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref called));
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
            localManagedIpV4: localManagedIpV4,
            localManagedIpsV6: Array.Empty<IPAddress>(),
            inlineCom: new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
}

