using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace JKamsker.LibZt.Sockets;

/// <summary>
/// Simple TCP port forwarder that accepts overlay TCP connections and forwards them to a local OS TCP endpoint.
/// </summary>
public sealed class ZtOverlayTcpPortForwarder : IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<Task> _connectionTasks = new();
    private readonly ZtOverlayTcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private bool _disposed;

    public ZtOverlayTcpPortForwarder(
        ZtNode node,
        ulong networkId,
        int overlayListenPort,
        string targetHost,
        int targetPort)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        if (targetPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPort));
        }

        OverlayListenPort = overlayListenPort;
        _listener = new ZtOverlayTcpListener(node, networkId, overlayListenPort);
        _targetHost = targetHost;
        _targetPort = targetPort;
    }

    public int OverlayListenPort { get; }

    public string TargetHost => _targetHost;

    public int TargetPort => _targetPort;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linkedCts.Token;

        while (!token.IsCancellationRequested)
        {
            ZtOverlayTcpClient accepted;
            try
            {
                accepted = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
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

            _connectionTasks.Add(HandleConnectionAsync(accepted, token));
        }

        if (!_connectionTasks.IsEmpty)
        {
            await Task.WhenAll(_connectionTasks.ToArray()).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _shutdown.CancelAsync().ConfigureAwait(false);
            await _listener.DisposeAsync().ConfigureAwait(false);

            if (!_connectionTasks.IsEmpty)
            {
                try
                {
                    await Task.WhenAll(_connectionTasks.ToArray()).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            _disposeLock.Release();
            _disposeLock.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task HandleConnectionAsync(ZtOverlayTcpClient accepted, CancellationToken cancellationToken)
    {
        var overlayClient = accepted;
        TcpClient? localClient = null;
        CancellationTokenSource? bridgeCts = null;
        Task? overlayToLocal = null;
        Task? localToOverlay = null;

        try
        {
            localClient = new TcpClient { NoDelay = true };
            await localClient.ConnectAsync(_targetHost, _targetPort, cancellationToken).ConfigureAwait(false);

            var localStream = localClient.GetStream();
            var overlayStream = overlayClient.GetStream();

            bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var bridgeToken = bridgeCts.Token;

            overlayToLocal = CopyAsync(overlayStream, localStream, bridgeToken);
            localToOverlay = CopyAsync(localStream, overlayStream, bridgeToken);

            _ = await Task.WhenAny(overlayToLocal, localToOverlay).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            if (bridgeCts is not null)
            {
                try
                {
                    await bridgeCts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (overlayToLocal is not null || localToOverlay is not null)
            {
                try
                {
                    await Task
                        .WhenAll(
                            overlayToLocal ?? Task.CompletedTask,
                            localToOverlay ?? Task.CompletedTask)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            bridgeCts?.Dispose();
            localClient?.Dispose();
            await overlayClient.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, bufferSize: 64 * 1024, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
