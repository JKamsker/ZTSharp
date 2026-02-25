using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli.Commands;

internal static partial class ExposeCommand
{
    private static async Task RunExposeAcceptorAsync(
        ZeroTierTcpListener listener,
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
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
                await HandleExposeConnectionAsync(accepted, targetHost, targetPort, cancellationToken).ConfigureAwait(false);
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

    private static async Task HandleExposeConnectionAsync(
        Stream accepted,
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        var overlayStream = accepted;
        var localClient = new System.Net.Sockets.TcpClient { NoDelay = true };

        try
        {
            await localClient.ConnectAsync(targetHost, targetPort, cancellationToken).ConfigureAwait(false);
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
                    try
                    {
                        await bridgeCts.CancelAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    try
                    {
                        await Task.WhenAll(overlayToLocal, localToOverlay).ConfigureAwait(false);
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
            finally
            {
                try
                {
                    await localStream.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            localClient.Dispose();

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

