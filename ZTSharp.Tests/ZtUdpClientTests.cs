using System.IO;
using ZTSharp.Sockets;

namespace ZTSharp.Tests;

public sealed class ZtUdpClientTests
{
    [Fact]
    public async Task InMemoryUdpClient_EchoesDatagram()
    {
        var n1Store = new MemoryStateStore();
        var n2Store = new MemoryStateStore();
        var networkId = 9001UL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = n1Store
        });
        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = n2Store
        });

        await node1.StartAsync();
        await node2.StartAsync();
        await node1.JoinNetworkAsync(networkId);
        await node2.JoinNetworkAsync(networkId);

        await using var node1Udp = new ZtUdpClient(node1, networkId, 10001);
        await using var node2Udp = new ZtUdpClient(node2, networkId, 10002);

        await node2Udp.ConnectAsync((await node1.GetIdentityAsync()).NodeId.Value, 10001);

        var receive = node1Udp.ReceiveAsync().AsTask();
        await node2Udp.SendAsync(new byte[] { 1, 2, 3, 4 });
        var datagram = await receive.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(datagram.Payload.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }));

        await node2.LeaveNetworkAsync(networkId);
        await node1.LeaveNetworkAsync(networkId);
        await node1.StopAsync();
        await node2.StopAsync();
    }

    [Fact]
    public async Task OsUdpUdpClient_EchoesDatagram()
    {
        var n1Store = new MemoryStateStore();
        var n2Store = new MemoryStateStore();
        var networkId = 9002UL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = n1Store,
            TransportMode = TransportMode.OsUdp
        });

        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = n2Store,
            TransportMode = TransportMode.OsUdp
        });

        await node1.StartAsync();
        await node2.StartAsync();
        await node1.JoinNetworkAsync(networkId);
        await node2.JoinNetworkAsync(networkId);

        var node1Id = (await node1.GetIdentityAsync()).NodeId.Value;
        var node2Id = (await node2.GetIdentityAsync()).NodeId.Value;
        var node1Endpoint = node1.LocalTransportEndpoint;
        var node2Endpoint = node2.LocalTransportEndpoint;
        Assert.NotNull(node1Endpoint);
        Assert.NotNull(node2Endpoint);

        await node1.AddPeerAsync(networkId, node2Id, node2Endpoint);
        await node2.AddPeerAsync(networkId, node1Id, node1Endpoint);

        await using var node1Udp = new ZtUdpClient(node1, networkId, 11001);
        await using var node2Udp = new ZtUdpClient(node2, networkId, 11002);

        await node2Udp.ConnectAsync(node1Id, 11001);

        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var receive = node1Udp.ReceiveAsync().AsTask();
        await node2Udp.SendAsync(payload);
        var datagram = await receive.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(datagram.Payload.Span.SequenceEqual(payload));

        await node2.LeaveNetworkAsync(networkId);
        await node1.LeaveNetworkAsync(networkId);
        await node1.StopAsync();
        await node2.StopAsync();
    }
}
