using ZTSharp.Internal;
using ZTSharp.Transport;

namespace ZTSharp.Tests;

public sealed class NodeNetworkLeaveOrderingTests
{
    [Fact]
    public async Task LeaveNetworkAsync_FailedLeave_DoesNotLoseRegistration_AndRetryUnsubscribes()
    {
        var store = new MemoryStateStore();
        var transport = new FlakyLeaveTransport();
        var events = new NodeEventStream(_ => { });
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
}
