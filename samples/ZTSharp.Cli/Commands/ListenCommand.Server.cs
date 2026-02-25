using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli.Commands;

internal static partial class ListenCommand
{
    private static async Task RunListenAcceptorAsync(ZeroTierTcpListener listener, long bodyBytes, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Stream accepted;
            try
            {
                accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            try
            {
                await HandleListenConnectionAsync(accepted, bodyBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task HandleListenConnectionAsync(Stream accepted, long bodyBytes, CancellationToken cancellationToken)
    {
        var overlayStream = accepted;
        try
        {
            var headers = await HttpUtilities.ReadHttpHeadersAsync(overlayStream, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(headers))
            {
                return;
            }

            var requestLine = headers;
            var firstLineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
            if (firstLineEnd >= 0)
            {
                requestLine = headers.Substring(0, firstLineEnd);
            }

            Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] {requestLine}");
            Console.WriteLine(headers);

            await HttpUtilities.WriteHttpOkAsync(overlayStream, bodyBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await overlayStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

