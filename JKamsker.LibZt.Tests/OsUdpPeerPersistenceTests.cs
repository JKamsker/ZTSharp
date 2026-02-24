using System.Text;
using JKamsker.LibZt.Sockets;

namespace JKamsker.LibZt.Tests;

public sealed class OsUdpPeerPersistenceTests
{
    [Fact]
    public async Task OsUdp_PeerDirectory_IsPersisted_AndRecovered_OnRestart()
    {
        var networkId = 0xBEEF1235UL;
        var nodeARoot = Path.Combine(Path.GetTempPath(), "zt-node-peer-persist-" + Guid.NewGuid());

        try
        {
            await using var nodeB = new Node(new NodeOptions
            {
                StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
                StateStore = new MemoryStateStore(),
                TransportMode = TransportMode.OsUdp,
                EnablePeerDiscovery = false
            });

            await nodeB.StartAsync();
            await nodeB.JoinNetworkAsync(networkId);

            var nodeBId = (await nodeB.GetIdentityAsync()).NodeId.Value;
            var nodeBEndpoint = nodeB.LocalTransportEndpoint;
            Assert.NotNull(nodeBEndpoint);

            await using var udpB = new ZtUdpClient(nodeB, networkId, 14002);

            await using (var nodeA = new Node(new NodeOptions
            {
                StateRootPath = nodeARoot,
                TransportMode = TransportMode.OsUdp,
                EnablePeerDiscovery = false
            }))
            {
                await nodeA.StartAsync();
                await nodeA.JoinNetworkAsync(networkId);

                await nodeA.AddPeerAsync(networkId, nodeBId, nodeBEndpoint);

                await using var udpA = new ZtUdpClient(nodeA, networkId, 14001);
                await udpA.ConnectAsync(nodeBId, 14002);

                var payload1 = Encoding.UTF8.GetBytes("one");
                var receive1 = udpB.ReceiveAsync().AsTask();
                await udpA.SendAsync(payload1);
                var datagram1 = await receive1.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(datagram1.Payload.Span.SequenceEqual(payload1));
            }

            await using var nodeA2 = new Node(new NodeOptions
            {
                StateRootPath = nodeARoot,
                TransportMode = TransportMode.OsUdp,
                EnablePeerDiscovery = false
            });

            await nodeA2.StartAsync();
            await nodeA2.JoinNetworkAsync(networkId);

            await using var udpA2 = new ZtUdpClient(nodeA2, networkId, 14003);
            await udpA2.ConnectAsync(nodeBId, 14002);

            var payload2 = Encoding.UTF8.GetBytes("two");
            var receive2 = udpB.ReceiveAsync().AsTask();
            await udpA2.SendAsync(payload2);
            var datagram2 = await receive2.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(datagram2.Payload.Span.SequenceEqual(payload2));
        }
        finally
        {
            if (Directory.Exists(nodeARoot))
            {
                Directory.Delete(nodeARoot, recursive: true);
            }
        }
    }
}

