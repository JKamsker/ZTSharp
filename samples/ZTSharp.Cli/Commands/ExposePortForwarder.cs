using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli.Commands;

internal sealed class ExposePortForwarder
{
    private readonly string _targetHost;
    private readonly int _targetPort;

    public ExposePortForwarder(string targetHost, int targetPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetPort);

        _targetHost = targetHost;
        _targetPort = targetPort;
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
        var localClient = new TcpClient { NoDelay = true };

        try
        {
            await localClient.ConnectAsync(_targetHost, _targetPort, cancellationToken).ConfigureAwait(false);
            var localStream = localClient.GetStream();
            try
            {
                using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = bridgeCts.Token;

                var overlayToLocal = StreamUtilities.CopyAsync(overlayStream, localStream, token);
                var localToOverlay = StreamUtilities.CopyAsync(localStream, overlayStream, token);

                try
                {
                    _ = await Task.WhenAny(overlayToLocal, localToOverlay).ConfigureAwait(false);
                }
                finally
                {
                    await bridgeCts.CancelAsync().ConfigureAwait(false);

                    try
                    {
                        await Task.WhenAll(overlayToLocal, localToOverlay).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
                    {
                    }
                }
            }
            finally
            {
                await localStream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            localClient.Dispose();

            await overlayStream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
