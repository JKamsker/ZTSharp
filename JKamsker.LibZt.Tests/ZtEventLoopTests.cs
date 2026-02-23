namespace JKamsker.LibZt.Tests;

public sealed class ZtEventLoopTests
{
    private static ValueTask SetTcs(object? state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ((TaskCompletionSource<bool>)state!).TrySetResult(true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EventLoop_ExecutesPostedWork()
    {
        using var loop = new ZtEventLoop(TimeSpan.FromMilliseconds(50));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        loop.Post(SetTcs, tcs);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task EventLoop_FiresTimers()
    {
        using var loop = new ZtEventLoop(TimeSpan.FromMilliseconds(20));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = loop.Schedule(TimeSpan.FromMilliseconds(50), SetTcs, tcs);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task EventLoop_CancelledTimer_DoesNotFire()
    {
        using var loop = new ZtEventLoop(TimeSpan.FromMilliseconds(20));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var timer = loop.Schedule(TimeSpan.FromMilliseconds(200), SetTcs, tcs);
        Assert.True(timer.Cancel());

        await Task.Delay(300);
        Assert.False(tcs.Task.IsCompleted);
    }
}

