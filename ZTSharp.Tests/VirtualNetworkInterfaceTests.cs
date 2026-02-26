namespace ZTSharp.Tests;

public sealed class VirtualNetworkInterfaceTests
{
    [Fact]
    public async Task InMemoryVirtualInterface_DeliversPacket()
    {
        var networkId = 0xCAFE0002UL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        await node1.StartAsync();
        await node2.StartAsync();
        await node1.JoinNetworkAsync(networkId);
        await node2.JoinNetworkAsync(networkId);

        await using var iface1 = new VirtualNetworkInterface(node1, networkId);
        await using var iface2 = new VirtualNetworkInterface(node2, networkId);

        var destination = (await node2.GetIdentityAsync()).NodeId.Value;
        var payload = new byte[] { 0x45, 0x00, 0x00, 0x14, 0x00, 0x01 };

        var receive = iface2.ReceivePacketAsync().AsTask();
        await iface1.SendPacketAsync(destination, payload);
        var packet = await receive.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal((await node1.GetIdentityAsync()).NodeId.Value, packet.SourceNodeId);
        Assert.True(packet.Payload.Span.SequenceEqual(payload));
    }
}
