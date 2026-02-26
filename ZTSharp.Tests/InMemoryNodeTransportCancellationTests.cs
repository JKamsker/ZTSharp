using ZTSharp.Transport;

namespace ZTSharp.Tests;

public sealed class InMemoryNodeTransportCancellationTests
{
    [Fact]
    public async Task SendFrameAsync_SenderCancellation_DoesNotCausePartialFanout()
    {
        using var transport = new InMemoryNodeTransport();
        var networkId = 0xCAFE4001UL;
        var delivered1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        _ = await transport.JoinNetworkAsync(
            networkId,
            nodeId: 2,
            onFrameReceived: (source, network, payload, token) =>
            {
                cts.Cancel();
                delivered1.TrySetResult(true);
                return Task.CompletedTask;
            });

        _ = await transport.JoinNetworkAsync(
            networkId,
            nodeId: 3,
            onFrameReceived: (source, network, payload, token) =>
            {
                delivered2.TrySetResult(true);
                return Task.CompletedTask;
            });

        await transport.SendFrameAsync(networkId, sourceNodeId: 1, payload: new byte[] { 1, 2, 3 }, cts.Token);

        await delivered1.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await delivered2.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
