using System.Text;
using ZTSharp.Sockets;
using ZTSharp.Transport.Internal;

namespace ZTSharp.Tests;

public sealed class OsUdpPeerDiscoveryTests
{
    [Fact]
    public async Task OsUdp_PeerDiscovery_AllowsUdpEcho_WithoutManualAddPeer()
    {
        var networkId = 0xBEEF1234UL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp
        });

        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp
        });

        await node1.StartAsync();
        await node2.StartAsync();
        await node1.JoinNetworkAsync(networkId);
        await node2.JoinNetworkAsync(networkId);

        await using var udp1 = new ZtUdpClient(node1, networkId, 12001);
        await using var udp2 = new ZtUdpClient(node2, networkId, 12002);

        var node1Id = (await node1.GetIdentityAsync()).NodeId.Value;
        var node2Id = (await node2.GetIdentityAsync()).NodeId.Value;

        await udp1.ConnectAsync(node2Id, 12002);
        await udp2.ConnectAsync(node1Id, 12001);

        var ping = Encoding.UTF8.GetBytes("ping");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var receivePing = udp2.ReceiveAsync(cts.Token).AsTask();
        var sender = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && !receivePing.IsCompleted)
            {
                await udp1.SendAsync(ping, cts.Token);
                await Task.Yield();
            }
        }, cts.Token);

        var datagramPing = await receivePing.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        cts.Cancel();
        try
        {
            await sender;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        Assert.True(datagramPing.Payload.Span.SequenceEqual(ping));
    }

    [Fact]
    public async Task OsUdp_DiscoveryMagicPrefix_DoesNotSwallowApplicationPayload()
    {
        var networkId = 0xBEEF5678UL;

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

        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        var payload = Encoding.UTF8.GetBytes("ZTC1-application-data");
        await node1.SendFrameAsync(networkId, payload);
        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(received.Span.SequenceEqual(payload));

        await node1.LeaveNetworkAsync(networkId);
        await node2.LeaveNetworkAsync(networkId);
        await node1.StopAsync();
        await node2.StopAsync();
    }

    [Fact]
    public async Task OsUdp_DiscoveryPayloadShape_DoesNotSwallowApplicationPayload()
    {
        var networkId = 0xBEEF9ABCUL;

        await using var node1 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp
        });

        await using var node2 = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore(),
            TransportMode = TransportMode.OsUdp
        });

        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        var payload = new byte[OsUdpPeerDiscoveryProtocol.PayloadLength];
        OsUdpPeerDiscoveryProtocol.WritePayload(
            OsUdpPeerDiscoveryProtocol.FrameType.PeerHello,
            node1Id.Value,
            networkId,
            payload);
        payload[^1] ^= 0xFF;

        await node1.SendFrameAsync(networkId, payload);
        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(received.Span.SequenceEqual(payload));

        await node1.LeaveNetworkAsync(networkId);
        await node2.LeaveNetworkAsync(networkId);
        await node1.StopAsync();
        await node2.StopAsync();
    }
}
