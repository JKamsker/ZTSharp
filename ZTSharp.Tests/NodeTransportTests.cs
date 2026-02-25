using System.IO;

namespace ZTSharp.Tests;

public sealed class NodeTransportTests
{
    [Fact]
    public async Task InMemoryTransport_DeliversFramesBetweenNodes()
    {
        var n1Store = new MemoryStateStore();
        var n2Store = new MemoryStateStore();
        var networkId = 424242UL;
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);

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

        node2.FrameReceived += (_, frame) =>
        {
            tcs.TrySetResult(frame.Payload);
        };

        await node1.StartAsync();
        await node2.StartAsync();
        await node1.JoinNetworkAsync(networkId);
        await node2.JoinNetworkAsync(networkId);

        await node1.SendFrameAsync(networkId, new byte[] { 1, 2, 3, 4, 5 });
        var payload = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(payload.Span.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }));

        await node1.LeaveNetworkAsync(networkId);
        await node2.LeaveNetworkAsync(networkId);
        await node1.StopAsync();
        await node2.StopAsync();
    }

    [Fact]
    public async Task OsUdpTransport_DeliversFramesBetweenNodes()
    {
        var n1Store = new MemoryStateStore();
        var n2Store = new MemoryStateStore();
        var networkId = 98765UL;
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);

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

        node2.FrameReceived += (_, frame) =>
        {
            if (frame.NetworkId == networkId)
            {
                tcs.TrySetResult(frame.Payload);
            }
        };

        await node1.StartAsync();
        await node2.StartAsync();
        await node1.JoinNetworkAsync(networkId);
        await node2.JoinNetworkAsync(networkId);

        var node1Id = (await node1.GetIdentityAsync()).NodeId;
        var node2Id = (await node2.GetIdentityAsync()).NodeId;

        var node1Endpoint = node1.LocalTransportEndpoint;
        var node2Endpoint = node2.LocalTransportEndpoint;
        Assert.NotNull(node1Endpoint);
        Assert.NotNull(node2Endpoint);

        await node1.AddPeerAsync(networkId, node2Id.Value, node2Endpoint);
        await node2.AddPeerAsync(networkId, node1Id.Value, node1Endpoint);

        await node1.SendFrameAsync(networkId, new byte[] { 10, 20, 30 });
        var payload = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(payload.Span.SequenceEqual(new byte[] { 10, 20, 30 }));

        await node1.LeaveNetworkAsync(networkId);
        await node2.LeaveNetworkAsync(networkId);
        await node1.StopAsync();
        await node2.StopAsync();
    }

    [Fact]
    public async Task OsUdpTransport_AutoDiscoversPeersWithoutManualAdd()
    {
        var n1Store = new MemoryStateStore();
        var n2Store = new MemoryStateStore();
        var networkId = 54321UL;
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);

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

        node2.FrameReceived += (_, frame) =>
        {
            if (frame.NetworkId == networkId)
            {
                tcs.TrySetResult(frame.Payload);
            }
        };

        await node1.StartAsync();
        await node2.StartAsync();
        await node1.JoinNetworkAsync(networkId);
        await node2.JoinNetworkAsync(networkId);

        await Task.Delay(100);
        await node1.SendFrameAsync(networkId, new byte[] { 9, 9, 9 });
        var payload = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(payload.Span.SequenceEqual(new byte[] { 9, 9, 9 }));

        await node1.LeaveNetworkAsync(networkId);
        await node2.LeaveNetworkAsync(networkId);
        await node1.StopAsync();
        await node2.StopAsync();
    }
}

