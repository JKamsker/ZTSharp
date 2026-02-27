namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpRemoteSendWindow
{
    private ushort _window = ushort.MaxValue;
    private TaskCompletionSource<bool>? _windowTcs;
    private Exception? _terminalException;
    private readonly object _lock = new();

    public ushort Window => _window;

    public void Update(ushort windowSize)
    {
        TaskCompletionSource<bool>? toRelease = null;
        lock (_lock)
        {
            _window = windowSize;
            if (windowSize != 0 && _windowTcs is not null)
            {
                toRelease = _windowTcs;
                _windowTcs = null;
            }
        }

        toRelease?.TrySetResult(true);
    }

    public void SignalWaiters(Exception exception)
    {
        TaskCompletionSource<bool>? toRelease = null;
        lock (_lock)
        {
            _terminalException ??= exception;
            if (_windowTcs is not null)
            {
                toRelease = _windowTcs;
                _windowTcs = null;
            }
        }

        toRelease?.TrySetException(exception);
    }

    public async Task WaitForNonZeroAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_terminalException is not null)
            {
                throw _terminalException;
            }

            if (_window != 0)
            {
                return;
            }

            TaskCompletionSource<bool> tcs;
            lock (_lock)
            {
                if (_terminalException is not null)
                {
                    throw _terminalException;
                }

                if (_window != 0)
                {
                    return;
                }

                tcs = _windowTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

