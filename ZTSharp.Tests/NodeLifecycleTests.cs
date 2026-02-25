using System.IO;

namespace ZTSharp.Tests;

public sealed class NodeLifecycleTests
{
    [Fact]
    public async Task Node_Start_JoinAndLeave_Workflow()
    {
        var store = new MemoryStateStore();
        var node = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
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
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
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
        var stateRoot = TestTempPaths.CreateGuidSuffixed("zt-node-");
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
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
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
}
