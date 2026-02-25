namespace ZTSharp.Cli;

internal static class StreamUtilities
{
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
