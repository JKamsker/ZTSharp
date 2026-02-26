using System.Net;

namespace ZTSharp.Tests;

public sealed class OsUdpSendFrameResilienceTests
{
    [Fact]
    public async Task OsUdpTransport_SendFrameAsync_IgnoresPerPeerSendFailures()
    {
        var networkId = 0xDEAD_BEEFUL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp,
            EnablePeerDiscovery = false,
            EnableIpv6 = false
        });

        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp,
            EnablePeerDiscovery = false,
            EnableIpv6 = false
        });

        var received = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        node2.FrameReceived += (_, frame) =>
        {
            if (frame.NetworkId == networkId)
            {
                received.TrySetResult(frame.Payload);
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

        // Add a second peer that will fail to send on an IPv4-only socket.
        await node1.AddPeerAsync(networkId, peerNodeId: 0xFEEDUL, new IPEndPoint(IPAddress.IPv6Loopback, node2Endpoint.Port));

        var payload = new byte[] { 0x7F };
        var sendEx = await Record.ExceptionAsync(() => node1.SendFrameAsync(networkId, payload));
        Assert.Null(sendEx);

        var receivedPayload = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(receivedPayload.Span.SequenceEqual(payload));
    }
}

