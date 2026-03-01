using ZTSharp.Internal;
using ZTSharp.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZTSharp.Tests;

public sealed class NodeNetworkLeaveOrderingTests
{
    [Fact]
    public async Task JoinNetworkAsync_DuplicateJoin_IsIdempotentAndDoesNotLeakRegistrations()
    {
        var store = new MemoryStateStore();
        var transport = new RecordingJoinTransport();
        var events = new NodeEventStream(_ => { }, NullLogger.Instance);
        var peers = new NodePeerService(store);
        var service = new NodeNetworkService(store, transport, events, peers);

        var networkId = 0xCAFE5003UL;

        await service.JoinNetworkAsync(
            networkId,
            localNodeId: 1,
            localEndpoint: null,
            onFrameReceived: (_, _, _, _) => Task.CompletedTask,
            CancellationToken.None);

        await service.JoinNetworkAsync(
            networkId,
            localNodeId: 1,
            localEndpoint: null,
            onFrameReceived: (_, _, _, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(1, transport.JoinCallCount);
    }

    [Fact]
    public async Task LeaveNetworkAsync_FailedLeave_DoesNotLoseRegistration_AndRetryUnsubscribes()
    {
        var store = new MemoryStateStore();
        var transport = new FlakyLeaveTransport();
        var events = new NodeEventStream(_ => { }, NullLogger.Instance);
        var peers = new NodePeerService(store);
        var service = new NodeNetworkService(store, transport, events, peers);

        var received = 0;
        var networkId = 0xCAFE5001UL;

        await service.JoinNetworkAsync(
            networkId,
            localNodeId: 1,
            localEndpoint: null,
            onFrameReceived: (_, _, _, _) =>
            {
                Interlocked.Increment(ref received);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        _ = await transport.Inner.JoinNetworkAsync(networkId, nodeId: 2, (_, _, _, _) => Task.CompletedTask);

        await transport.Inner.SendFrameAsync(networkId, sourceNodeId: 2, payload: new byte[] { 1 });
        Assert.Equal(1, received);

        transport.FailNextLeave = true;
        await Assert.ThrowsAsync<IOException>(() => service.LeaveNetworkAsync(networkId, CancellationToken.None));

        await transport.Inner.SendFrameAsync(networkId, sourceNodeId: 2, payload: new byte[] { 2 });
        Assert.Equal(2, received);

        await service.LeaveNetworkAsync(networkId, CancellationToken.None);

        await transport.Inner.SendFrameAsync(networkId, sourceNodeId: 2, payload: new byte[] { 3 });
        Assert.Equal(2, received);
    }

    [Fact]
    public async Task LeaveNetworkAsync_ConcurrentCalls_InvokeTransportLeaveOnce()
    {
        var store = new MemoryStateStore();
        var transport = new BlockingLeaveTransport();
        var events = new NodeEventStream(_ => { }, NullLogger.Instance);
        var peers = new NodePeerService(store);
        var service = new NodeNetworkService(store, transport, events, peers);

        var networkId = 0xCAFE5002UL;

        await service.JoinNetworkAsync(
            networkId,
            localNodeId: 1,
            localEndpoint: null,
            onFrameReceived: (_, _, _, _) => Task.CompletedTask,
            CancellationToken.None);

        var leave1 = service.LeaveNetworkAsync(networkId, CancellationToken.None);
        await transport.LeaveEntered;

        var leave2 = service.LeaveNetworkAsync(networkId, CancellationToken.None);

        await Task.Delay(25);
        Assert.Equal(1, transport.LeaveCallCount);

        transport.AllowLeaveToProceed();
        await Task.WhenAll(leave1, leave2);

        Assert.Equal(1, transport.LeaveCallCount);
    }

    private sealed class FlakyLeaveTransport : INodeTransport, IDisposable
    {
        public InMemoryNodeTransport Inner { get; } = new();

        public bool FailNextLeave { get; set; }

        public Task<Guid> JoinNetworkAsync(
            ulong networkId,
            ulong nodeId,
            Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> onFrameReceived,
            System.Net.IPEndPoint? localEndpoint = null,
            CancellationToken cancellationToken = default)
            => Inner.JoinNetworkAsync(networkId, nodeId, onFrameReceived, localEndpoint, cancellationToken);

        public Task LeaveNetworkAsync(ulong networkId, Guid registrationId, CancellationToken cancellationToken = default)
        {
            if (FailNextLeave)
            {
                FailNextLeave = false;
                throw new IOException("synthetic leave failure");
            }

            return Inner.LeaveNetworkAsync(networkId, registrationId, cancellationToken);
        }

        public Task SendFrameAsync(ulong networkId, ulong sourceNodeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => Inner.SendFrameAsync(networkId, sourceNodeId, payload, cancellationToken);

        public Task FlushAsync(CancellationToken cancellationToken = default)
            => Inner.FlushAsync(cancellationToken);

        public void Dispose() => Inner.Dispose();
    }

    private sealed class BlockingLeaveTransport : INodeTransport, IDisposable
    {
        private readonly TaskCompletionSource _leaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowLeaveToProceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _leaveEnteredSet;
        private int _leaveCallCount;

        public InMemoryNodeTransport Inner { get; } = new();

        public int LeaveCallCount => Volatile.Read(ref _leaveCallCount);

        public Task LeaveEntered => _leaveEntered.Task;

        public void AllowLeaveToProceed() => _allowLeaveToProceed.TrySetResult();

        public Task<Guid> JoinNetworkAsync(
            ulong networkId,
            ulong nodeId,
            Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> onFrameReceived,
            System.Net.IPEndPoint? localEndpoint = null,
            CancellationToken cancellationToken = default)
            => Inner.JoinNetworkAsync(networkId, nodeId, onFrameReceived, localEndpoint, cancellationToken);

        public async Task LeaveNetworkAsync(ulong networkId, Guid registrationId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _leaveCallCount);
            if (Interlocked.Exchange(ref _leaveEnteredSet, 1) == 0)
            {
                _leaveEntered.TrySetResult();
            }

            await _allowLeaveToProceed.Task.ConfigureAwait(false);
            await Inner.LeaveNetworkAsync(networkId, registrationId, cancellationToken).ConfigureAwait(false);
        }

        public Task SendFrameAsync(ulong networkId, ulong sourceNodeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => Inner.SendFrameAsync(networkId, sourceNodeId, payload, cancellationToken);

        public Task FlushAsync(CancellationToken cancellationToken = default)
            => Inner.FlushAsync(cancellationToken);

        public void Dispose() => Inner.Dispose();
    }

    private sealed class RecordingJoinTransport : INodeTransport
    {
        private int _joinCallCount;

        public int JoinCallCount => Volatile.Read(ref _joinCallCount);

        public Task<Guid> JoinNetworkAsync(
            ulong networkId,
            ulong nodeId,
            Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> onFrameReceived,
            System.Net.IPEndPoint? localEndpoint = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _joinCallCount);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task LeaveNetworkAsync(ulong networkId, Guid registrationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendFrameAsync(ulong networkId, ulong sourceNodeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task FlushAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
