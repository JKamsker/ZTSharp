using System.Net.Sockets;
using System.Net;
using ZTSharp.Sockets;
using ZTSharp.Transport;

namespace ZTSharp.Tests;

public sealed class OsUdpSpoofingTests
{
    [Fact]
    public async Task OsUdpTransport_DropsSpoofedSourceNodeId_ForUdpHandlers()
    {
        var networkId = 0xCAFE7001UL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp,
            EnableIpv6 = false
        });

        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp,
            EnableIpv6 = false
        });

        await node1.StartAsync();
        await node2.StartAsync();
        await node1.JoinNetworkAsync(networkId);
        await node2.JoinNetworkAsync(networkId);

        var node1Id = (await node1.GetIdentityAsync()).NodeId.Value;
        var node2Id = (await node2.GetIdentityAsync()).NodeId.Value;
        var node2Endpoint = node2.LocalTransportEndpoint;
        Assert.NotNull(node2Endpoint);

        await using var udp2 = new ZtUdpClient(node2, networkId, localPort: 12002);

        var datagram = "spoof"u8.ToArray();
        var udpPayload = new byte[14 + datagram.Length];
        udpPayload[0] = 2; // v2
        udpPayload[1] = 1; // datagram type
        udpPayload[2] = 0x2E; // 12001
        udpPayload[3] = 0xE1;
        udpPayload[4] = 0x2E; // 12002
        udpPayload[5] = 0xE2;
        BitConverter.GetBytes(node2Id).CopyTo(udpPayload, 6);
        datagram.CopyTo(udpPayload, 14);

        var frameBuffer = new byte[NodeFrameCodec.GetEncodedLength(udpPayload.Length)];
        Assert.True(NodeFrameCodec.TryEncode(networkId, node1Id, udpPayload, frameBuffer, out var frameLength));

        using (var spoofUdp = CreateSpoofUdp(node2Endpoint!))
        {
            await spoofUdp.SendAsync(frameBuffer.AsMemory(0, frameLength), node2Endpoint!);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => udp2.ReceiveAsync(cts.Token).AsTask());
    }

    [Fact]
    public async Task OsUdpTransport_DropsSpoofedSourceNodeId_ForOverlayTcpListeners()
    {
        var networkId = 0xCAFE7002UL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp,
            EnableIpv6 = false
        });

        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp,
            EnableIpv6 = false
        });

        await node1.StartAsync();
        await node2.StartAsync();
        await node1.JoinNetworkAsync(networkId);
        await node2.JoinNetworkAsync(networkId);

        await using var listener = new OverlayTcpListener(node2, networkId, localPort: 20000);

        var node1Id = (await node1.GetIdentityAsync()).NodeId.Value;
        var node2Id = (await node2.GetIdentityAsync()).NodeId.Value;

        var synPayload = new byte[OverlayTcpFrameCodec.HeaderLength];
        OverlayTcpFrameCodec.BuildHeader(
            OverlayTcpFrameCodec.FrameType.Syn,
            sourcePort: 20001,
            destinationPort: 20000,
            destinationNodeId: node2Id,
            connectionId: 123UL,
            synPayload);

        var frameBuffer = new byte[NodeFrameCodec.GetEncodedLength(synPayload.Length)];
        Assert.True(NodeFrameCodec.TryEncode(networkId, node1Id, synPayload, frameBuffer, out var frameLength));

        var node2Endpoint = node2.LocalTransportEndpoint;
        Assert.NotNull(node2Endpoint);

        using (var spoofUdp = CreateSpoofUdp(node2Endpoint!))
        {
            await spoofUdp.SendAsync(frameBuffer.AsMemory(0, frameLength), node2Endpoint!);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.AcceptTcpClientAsync(cts.Token).AsTask());
    }

    private static UdpClient CreateSpoofUdp(System.Net.IPEndPoint destination)
    {
        if (destination.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var udp = new UdpClient(System.Net.Sockets.AddressFamily.InterNetworkV6);
            udp.Client.DualMode = true;
            udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Any, 0));
            return udp;
        }

        var v4 = new UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        var spoofAddress = destination.Address.Equals(IPAddress.Parse("127.0.0.2"))
            ? IPAddress.Parse("127.0.0.3")
            : IPAddress.Parse("127.0.0.2");
        v4.Client.Bind(new System.Net.IPEndPoint(spoofAddress, 0));
        return v4;
    }
}
