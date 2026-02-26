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

        await Task.Delay(10);
        Assert.False(waitTask.IsCompleted);

        second.TrySetResult(true);
        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
    }
}

