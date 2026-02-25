namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpWindowUpdateTrigger
{
    private readonly UserSpaceTcpReceiver _receiver;
    private readonly Func<uint, CancellationToken, Task> _sendPureAckAsync;
    private readonly Func<ushort> _getLastAdvertisedWindow;
    private readonly Func<bool> _isDisposed;

    private int _pending;

    public UserSpaceTcpWindowUpdateTrigger(
        UserSpaceTcpReceiver receiver,
        Func<uint, CancellationToken, Task> sendPureAckAsync,
        Func<ushort> getLastAdvertisedWindow,
        Func<bool> isDisposed)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(sendPureAckAsync);
        ArgumentNullException.ThrowIfNull(getLastAdvertisedWindow);
        ArgumentNullException.ThrowIfNull(isDisposed);

        _receiver = receiver;
        _sendPureAckAsync = sendPureAckAsync;
        _getLastAdvertisedWindow = getLastAdvertisedWindow;
        _isDisposed = isDisposed;
    }

    public void MaybeSendWindowUpdate(bool isConnected, bool isRemoteClosed)
    {
        if (!isConnected || _isDisposed() || isRemoteClosed)
        {
            return;
        }

        if (_getLastAdvertisedWindow() != 0)
        {
            return;
        }

        var window = _receiver.GetReceiveWindow();
        if (window == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _sendPureAckAsync(_receiver.RecvNext, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _pending, 0);
            }
        });
    }
}

