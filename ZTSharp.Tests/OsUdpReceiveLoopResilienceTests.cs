using System.Threading;

namespace ZTSharp.Tests;

public sealed class OsUdpReceiveLoopResilienceTests
{
    [Fact]
    public async Task OsUdpTransport_ContinuesReceiving_WhenFrameHandlerThrows()
    {
        var networkId = 0x1234_5678UL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp,
            EnablePeerDiscovery = false
        });

        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp,
            EnablePeerDiscovery = false
        });

        var received = 0;
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        node2.FrameReceived += (_, frame) =>
        {
            if (frame.NetworkId != networkId)
            {
                return;
            }

            var callCount = Interlocked.Increment(ref received);
            if (callCount == 1)
            {
                throw new InvalidOperationException("Boom");
            }

            tcs.TrySetResult(frame.Payload);
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

        await node1.SendFrameAsync(networkId, new byte[] { 1 });
        await node1.SendFrameAsync(networkId, new byte[] { 2 });

        var payload = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(payload.Span.SequenceEqual(new byte[] { 2 }));

        await node1.LeaveNetworkAsync(networkId);
        await node2.LeaveNetworkAsync(networkId);
        await node1.StopAsync();
        await node2.StopAsync();
    }
}

