namespace ZTSharp.Tests;

public sealed class EventLoopTests
{
    private static ValueTask SetTcs(object? state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ((TaskCompletionSource<bool>)state!).TrySetResult(true);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ThrowAfterSignal(object? state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ((TaskCompletionSource<bool>)state!).TrySetResult(true);
        throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task EventLoop_ExecutesPostedWork()
    {
        using var loop = new EventLoop(TimeSpan.FromMilliseconds(50));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        loop.Post(SetTcs, tcs);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task EventLoop_FiresTimers()
    {
        using var loop = new EventLoop(TimeSpan.FromMilliseconds(20));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = loop.Schedule(TimeSpan.FromMilliseconds(50), SetTcs, tcs);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task EventLoop_CancelledTimer_DoesNotFire()
    {
        using var loop = new EventLoop(TimeSpan.FromMilliseconds(20));
        var cancelledTimerFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpointFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var timer = loop.Schedule(TimeSpan.FromMilliseconds(200), SetTcs, cancelledTimerFired);
        Assert.True(timer.Cancel());

        _ = loop.Schedule(TimeSpan.FromMilliseconds(300), SetTcs, checkpointFired);

        await checkpointFired.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(cancelledTimerFired.Task.IsCompleted);
    }

    [Fact]
    public async Task EventLoop_CallbackThrow_FaultsLoopAndSurfacesFailureOnPost()
    {
        using var loop = new EventLoop(TimeSpan.FromMilliseconds(10));
        var executed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        loop.Post(ThrowAfterSignal, executed);
        await executed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                loop.Post(SetTcs, new TaskCompletionSource<bool>());
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("EventLoop did not fault within the expected time.");
            }

            await Task.Yield();
        }
    }
}
