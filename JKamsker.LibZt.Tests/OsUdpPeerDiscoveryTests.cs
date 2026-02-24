using System.Text;
using JKamsker.LibZt.Sockets;

namespace JKamsker.LibZt.Tests;

public sealed class OsUdpPeerDiscoveryTests
{
    [Fact]
    public async Task OsUdp_PeerDiscovery_AllowsUdpEcho_WithoutManualAddPeer()
    {
        var networkId = 0xBEEF1234UL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp
        });

        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp
        });

        await node1.StartAsync();
        await node2.StartAsync();
        await node1.JoinNetworkAsync(networkId);
        await node2.JoinNetworkAsync(networkId);

        await Task.Delay(100);

        await using var udp1 = new UdpClient(node1, networkId, 12001);
        await using var udp2 = new UdpClient(node2, networkId, 12002);

        var node1Id = (await node1.GetIdentityAsync()).NodeId.Value;
        var node2Id = (await node2.GetIdentityAsync()).NodeId.Value;

        await udp1.ConnectAsync(node2Id, 12002);
        await udp2.ConnectAsync(node1Id, 12001);

        var ping = Encoding.UTF8.GetBytes("ping");

        var receivePing = udp2.ReceiveAsync();
        await udp1.SendAsync(ping);
        var datagramPing = await receivePing.AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(datagramPing.Payload.Span.SequenceEqual(ping));
    }
}
