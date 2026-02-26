namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierTimeouts
{
    public static async ValueTask<T> RunWithTimeoutAsync<T>(
        TimeSpan timeout,
        string operation,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be greater than zero.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await action(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{operation} timed out after {timeout}.");
        }
    }
}
