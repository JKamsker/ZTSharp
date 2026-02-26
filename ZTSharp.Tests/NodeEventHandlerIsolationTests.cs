namespace ZTSharp.Tests;

public sealed class NodeEventHandlerIsolationTests
{
    [Fact]
    public async Task EventHandlerReentrancy_DoesNotDeadlock_JoinNetwork()
    {
        var networkId = 0xCAFE6001UL;
        await using var node = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        var joinedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.EventRaised += (_, e) =>
        {
            if (e.Code != EventCode.NodeStarted)
            {
                return;
            }

            try
            {
                node.JoinNetworkAsync(networkId).GetAwaiter().GetResult();
                joinedTcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                joinedTcs.TrySetException(ex);
            }
        };

        await node.StartAsync();

        await joinedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var networks = await node.GetNetworksAsync();
        Assert.Contains(networkId, networks);
    }

    [Fact]
    public async Task EventHandlerExceptions_DoNotFaultNodeOperations()
    {
        await using var node = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        node.EventRaised += (_, _) => throw new InvalidOperationException("user handler failed");

        await node.StartAsync();
        Assert.True(node.IsRunning);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotWedge_WhenStopPathsBlock()
    {
        var store = new BlockOnFlushStore();
        await using var node = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = store
        });

        await node.StartAsync();
        await node.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class BlockOnFlushStore : IStateStore
    {
        private readonly MemoryStateStore _inner = new();

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);

        public Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken cancellationToken = default) => _inner.ReadAsync(key, cancellationToken);

        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => _inner.WriteAsync(key, value, cancellationToken);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);

        public Task<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, cancellationToken);

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}

