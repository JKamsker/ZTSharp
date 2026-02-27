using ZTSharp.Internal;

namespace ZTSharp.Tests;

public sealed class ActiveTaskSetTests
{
    [Fact]
    public async Task WaitAsync_WaitsForTasksTrackedDuringWait()
    {
        var set = new ActiveTaskSet();

        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        set.Track(first.Task);
        var waitTask = set.WaitAsync();

        set.Track(second.Task);
        first.TrySetResult(true);

        await Task.Yield();
        await Task.Yield();
        Assert.False(waitTask.IsCompleted);

        second.TrySetResult(true);
        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitAsync_DoesNotThrow_WhenTrackedTaskFaults()
    {
        var set = new ActiveTaskSet();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        set.Track(tcs.Task);

        tcs.TrySetException(new InvalidOperationException("boom"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await set.WaitAsync(cts.Token);
    }
}

