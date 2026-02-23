using System.IO;
using JKamsker.LibZt;

namespace JKamsker.LibZt.Tests;

public class StoreAndNodeTests
{
    [Fact]
    public async Task FileStore_UsesRootsAlias()
    {
        var path = Path.Combine(Path.GetTempPath(), "zt-store-alias-" + Guid.NewGuid());
        try
        {
            var store = new FileZtStateStore(path);
            await store.WriteAsync("roots", [1, 2, 3, 4]);
            var readViaPlanet = await store.ReadAsync("planet");
            var listed = await store.ListAsync();

            Assert.NotNull(readViaPlanet);
            Assert.Equal([1, 2, 3, 4], readViaPlanet);
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
        var store = new MemoryZtStateStore();
        await store.WriteAsync("foo/bar", [42, 43, 44]);
        var exists = await store.ExistsAsync("foo/bar");
        var value = await store.ReadAsync("foo/bar");
        var list = await store.ListAsync("foo");

        Assert.True(exists);
        Assert.Equal([42, 43, 44], value);
        Assert.Contains("foo/bar", list);

        var deleted = await store.DeleteAsync("foo/bar");
        var missing = await store.ReadAsync("foo/bar");

        Assert.True(deleted);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Node_Start_JoinAndLeave_Workflow()
    {
        var store = new MemoryZtStateStore();
        var node = new ZtNode(new ZtNodeOptions
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
        var store = new MemoryZtStateStore();
        ZtNodeId firstId;
        var options = new ZtNodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = store
        };

        await using (var first = new ZtNode(options))
        {
            await first.StartAsync();
            var identity = await first.GetIdentityAsync();
            firstId = identity.NodeId;
            await first.StopAsync();
        }

        await using (var second = new ZtNode(options))
        {
            await second.StartAsync();
            var secondId = (await second.GetIdentityAsync()).NodeId;
            Assert.Equal(firstId, secondId);
        }
    }

    [Fact]
    public async Task InMemoryTransport_DeliversFramesBetweenNodes()
    {
        var n1Store = new MemoryZtStateStore();
        var n2Store = new MemoryZtStateStore();
        var networkId = 424242UL;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var node1 = new ZtNode(new ZtNodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = n1Store
        });
        await using var node2 = new ZtNode(new ZtNodeOptions
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

        await node1.SendFrameAsync(networkId, [1, 2, 3, 4, 5]);
        var payload = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal([1, 2, 3, 4, 5], payload);

        await node1.LeaveNetworkAsync(networkId);
        await node2.LeaveNetworkAsync(networkId);
        await node1.StopAsync();
        await node2.StopAsync();
    }
}
