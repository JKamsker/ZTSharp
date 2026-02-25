using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli.Commands;

internal sealed class ListenHttpServer
{
    private readonly long _bodyBytes;

    public ListenHttpServer(long bodyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bodyBytes);
        _bodyBytes = bodyBytes;
    }

    public async Task RunAcceptorAsync(ZeroTierTcpListener listener, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listener);

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
                await HandleConnectionAsync(accepted, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task HandleConnectionAsync(Stream accepted, CancellationToken cancellationToken)
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

            await HttpUtilities.WriteHttpOkAsync(overlayStream, _bodyBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await overlayStream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
