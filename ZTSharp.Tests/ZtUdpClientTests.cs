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
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = n1Store
        });
        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
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
    public async Task InMemoryUdpClient_SendTo_IsDirected_WithV2Frames()
    {
        var networkId = 9003UL;

        await using var nodeA = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        await using var nodeB = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        await using var nodeC = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        await nodeA.StartAsync();
        await nodeB.StartAsync();
        await nodeC.StartAsync();
        await nodeA.JoinNetworkAsync(networkId);
        await nodeB.JoinNetworkAsync(networkId);
        await nodeC.JoinNetworkAsync(networkId);

        var nodeBId = (await nodeB.GetIdentityAsync()).NodeId.Value;

        const int sharedPort = 12000;

        await using var udpA = new ZtUdpClient(nodeA, networkId, 12001);
        await using var udpB = new ZtUdpClient(nodeB, networkId, sharedPort);
        await using var udpC = new ZtUdpClient(nodeC, networkId, sharedPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var receiveC = udpC.ReceiveAsync(cts.Token).AsTask();

        var payload = new byte[] { 1, 2, 3 };
        var receiveB = udpB.ReceiveAsync().AsTask();
        await udpA.SendToAsync(payload, nodeBId, sharedPort);

        var datagramB = await receiveB.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(datagramB.Payload.Span.SequenceEqual(payload));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiveC);

        await nodeA.LeaveNetworkAsync(networkId);
        await nodeB.LeaveNetworkAsync(networkId);
        await nodeC.LeaveNetworkAsync(networkId);
        await nodeA.StopAsync();
        await nodeB.StopAsync();
        await nodeC.StopAsync();
    }

    [Fact]
    public async Task OsUdpUdpClient_EchoesDatagram()
    {
        var n1Store = new MemoryStateStore();
        var n2Store = new MemoryStateStore();
        var networkId = 9002UL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = n1Store,
            TransportMode = TransportMode.OsUdp
        });

        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
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
