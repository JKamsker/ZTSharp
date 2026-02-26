using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class ZeroTierTimeoutsTests
{
    [Fact]
    public async Task RunWithTimeoutAsync_DoesNotMapUnrelatedOperationCanceled_ToTimeoutException()
    {
        var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            _ = await ZeroTierTimeouts.RunWithTimeoutAsync<int>(
                timeout: TimeSpan.FromSeconds(10),
                operation: "test",
                action: _ => throw new OperationCanceledException(),
                cancellationToken: CancellationToken.None);
        });

        Assert.False(ex.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task RunWithTimeoutAsync_MapsTimeoutCancellation_ToTimeoutException()
    {
        _ = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            _ = await ZeroTierTimeouts.RunWithTimeoutAsync<int>(
                timeout: TimeSpan.FromMilliseconds(100),
                operation: "test",
                action: async ct =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                },
                cancellationToken: CancellationToken.None);
        });
    }
}
