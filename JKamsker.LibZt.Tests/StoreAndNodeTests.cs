using System.IO;
using System.Net;
using System.Text;
using JKamsker.LibZt;
using JKamsker.LibZt.Sockets;

namespace JKamsker.LibZt.Tests;

public class StoreAndNodeTests
{
    [Fact]
    public async Task FileStore_UsesRootsAlias()
    {
        var path = Path.Combine(Path.GetTempPath(), "zt-store-alias-" + Guid.NewGuid());
        try
        {
            var store = new FileStateStore(path);
            await store.WriteAsync("roots", new byte[] { 1, 2, 3, 4 });
            var readViaPlanet = await store.ReadAsync("planet");
            var listed = await store.ListAsync();

            Assert.NotNull(readViaPlanet);
            Assert.True(readViaPlanet!.Value.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
            Assert.Contains("roots", listed);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task MemoryStore_Roundtrip()
    {
        var store = new MemoryStateStore();
        await store.WriteAsync("foo/bar", new byte[] { 42, 43, 44 });
        var exists = await store.ExistsAsync("foo/bar");
        var value = await store.ReadAsync("foo/bar");
        var list = await store.ListAsync("foo");

        Assert.True(exists);
        Assert.True(value!.Value.Span.SequenceEqual(new byte[] { 42, 43, 44 }));
        Assert.Contains("foo/bar", list);

        var deleted = await store.DeleteAsync("foo/bar");
        var missing = await store.ReadAsync("foo/bar");

        Assert.True(deleted);
        Assert.Null(missing);
    }

    [Fact]
    public async Task MemoryStore_UsesRootsAlias()
    {
        var store = new MemoryStateStore();
        await store.WriteAsync("roots", new byte[] { 1, 2, 3, 4 });
        var readViaPlanet = await store.ReadAsync("planet");
        var listed = await store.ListAsync();

        Assert.NotNull(readViaPlanet);
        Assert.True(readViaPlanet!.Value.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Assert.Contains("roots", listed);
    }

    [Fact]
    public async Task Node_Start_JoinAndLeave_Workflow()
    {
        var store = new MemoryStateStore();
        var node = new Node(new NodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = store
        });

        await node.StartAsync();
        Assert.True(node.IsRunning);

        await node.JoinNetworkAsync(123456UL);
        var networks = await node.GetNetworksAsync();
        Assert.Contains(123456UL, networks);

        await node.LeaveNetworkAsync(123456UL);
        networks = await node.GetNetworksAsync();
        Assert.DoesNotContain(123456UL, networks);
        await node.StopAsync();
    }

    [Fact]
    public async Task Node_Identity_IsStableAcrossRestart()
    {
        var store = new MemoryStateStore();
        NodeId firstId;
        var options = new NodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = store
        };

        await using (var first = new Node(options))
        {
            await first.StartAsync();
            var identity = await first.GetIdentityAsync();
            firstId = identity.NodeId;
            await first.StopAsync();
        }

        await using (var second = new Node(options))
        {
            await second.StartAsync();
            var secondId = (await second.GetIdentityAsync()).NodeId;
            Assert.Equal(firstId, secondId);
        }
    }

    [Fact]
    public async Task Node_RecoversNetworksAcrossRestart()
    {
        var store = new MemoryStateStore();
        var stateRoot = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid());
        var networkId = 1357911UL;

        await using (var first = new Node(new NodeOptions
        {
            StateRootPath = stateRoot,
            StateStore = store
        }))
        {
            await first.StartAsync();
            await first.JoinNetworkAsync(networkId);
            await first.StopAsync();
        }

        await using (var second = new Node(new NodeOptions
        {
            StateRootPath = stateRoot,
            StateStore = store
        }))
        {
            await second.StartAsync();
            var networks = await second.GetNetworksAsync();
            Assert.Contains(networkId, networks);
        }
    }

    [Fact]
    public async Task Node_EventStream_YieldsEvents_WhenSubscribed()
    {
        var store = new MemoryStateStore();
        await using var node = new Node(new NodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = store
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = node.GetEventStream(cts.Token).GetAsyncEnumerator(cts.Token);

        await node.StartAsync(cts.Token);

        var sawStarting = false;
        var sawStarted = false;
        for (var i = 0; i < 5; i++)
        {
            Assert.True(await enumerator.MoveNextAsync());
            sawStarting |= enumerator.Current.Code == EventCode.NodeStarting;
            sawStarted |= enumerator.Current.Code == EventCode.NodeStarted;
            if (sawStarting && sawStarted)
            {
                break;
            }
        }

        Assert.True(sawStarting);
        Assert.True(sawStarted);
    }

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

    [Fact]
    public async Task TcpListener_EchoesPayloadOffline()
    {
        await using var listener = new ZtTcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = listener.LocalEndpoint.Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var client = new ZtTcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var server = await acceptTask;

        var request = Encoding.UTF8.GetBytes("ping");
        var stream = client.GetStream();
        await stream.WriteAsync(request, 0, request.Length);

        var serverStream = server.GetStream();
        var buffer = new byte[request.Length];
        var read = await serverStream.ReadAsync(buffer.AsMemory(0, request.Length));
        Assert.Equal(request.Length, read);

        await serverStream.WriteAsync(buffer.AsMemory(0, read));
        var response = new byte[request.Length];
        var responseRead = await stream.ReadAsync(response.AsMemory(0, request.Length));
        Assert.Equal(request.Length, responseRead);
        Assert.Equal(request, response);
    }
}
