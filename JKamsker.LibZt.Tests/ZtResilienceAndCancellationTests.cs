namespace JKamsker.LibZt.Tests;

public sealed class ZtResilienceAndCancellationTests
{
    [Fact]
    public async Task StopAsync_Cancellation_FaultsNodeState()
    {
        var store = new BlockOnFlushStore();
        await using var node = new ZtNode(new ZtNodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = store
        });

        await node.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.StopAsync(cts.Token));
        Assert.Equal(ZtNodeState.Faulted, node.State);
    }

    [Fact]
    public async Task JoinNetworkAsync_Cancellation_DoesNotLeaveNodeJoined()
    {
        const ulong networkId = 123456789UL;

        var store = new BlockOnNetworkWriteStore();
        await using var node = new ZtNode(new ZtNodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = store
        });

        await node.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.JoinNetworkAsync(networkId, cts.Token));

        var networks = await node.GetNetworksAsync();
        Assert.DoesNotContain(networkId, networks);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.SendFrameAsync(networkId, new byte[] { 1 }));
    }

    private sealed class BlockOnFlushStore : IZtStateStore
    {
        private readonly MemoryZtStateStore _inner = new();

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);

        public Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken cancellationToken = default) => _inner.ReadAsync(key, cancellationToken);

        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => _inner.WriteAsync(key, value, cancellationToken);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);

        public Task<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, cancellationToken);

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class BlockOnNetworkWriteStore : IZtStateStore
    {
        private readonly MemoryZtStateStore _inner = new();

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);

        public Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken cancellationToken = default) => _inner.ReadAsync(key, cancellationToken);

        public async Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            if (key.StartsWith("networks.d/", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            await _inner.WriteAsync(key, value, cancellationToken);
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);

        public Task<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, cancellationToken);

        public Task FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);
    }
}
