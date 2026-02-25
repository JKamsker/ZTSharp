namespace ZTSharp.Cli;

internal static class StreamUtilities
{
    public static async Task BridgeDuplexAsync(Stream left, Stream right, CancellationToken cancellationToken)
    {
        using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = bridgeCts.Token;

        var leftToRight = CopyAsync(left, right, token);
        var rightToLeft = CopyAsync(right, left, token);

        _ = await Task.WhenAny(leftToRight, rightToLeft).ConfigureAwait(false);
        await bridgeCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(leftToRight, rightToLeft).ConfigureAwait(false);
    }

    public static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, bufferSize: 64 * 1024, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
