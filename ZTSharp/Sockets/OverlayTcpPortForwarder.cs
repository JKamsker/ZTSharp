using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using SystemTcpClient = System.Net.Sockets.TcpClient;

namespace ZTSharp.Sockets;

/// <summary>
/// Simple TCP port forwarder that accepts overlay TCP connections and forwards them to a local OS TCP endpoint.
/// </summary>
public sealed class OverlayTcpPortForwarder : IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<Task> _connectionTasks = new();
    private readonly OverlayTcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private bool _disposed;

    public OverlayTcpPortForwarder(
        Node node,
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
        _listener = new OverlayTcpListener(node, networkId, overlayListenPort);
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
            OverlayTcpClient accepted;
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

    private async Task HandleConnectionAsync(OverlayTcpClient accepted, CancellationToken cancellationToken)
    {
        var overlayClient = accepted;
        using var localClient = new SystemTcpClient { NoDelay = true };

        CancellationTokenSource? bridgeCts = null;
        Stream? localStream = null;
        Stream? overlayStream = null;
        Task? overlayToLocal = null;
        Task? localToOverlay = null;

        try
        {
            await localClient.ConnectAsync(_targetHost, _targetPort, cancellationToken).ConfigureAwait(false);

            localStream = localClient.GetStream();
            overlayStream = overlayClient.GetStream();

            bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var bridgeToken = bridgeCts.Token;

#pragma warning disable CA2025 // Streams are disposed after the copy tasks complete.
            overlayToLocal = CopyAsync(overlayStream, localStream, bridgeToken);
            localToOverlay = CopyAsync(localStream, overlayStream, bridgeToken);
#pragma warning restore CA2025

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

            if (overlayStream is not null)
            {
                try
                {
                    await overlayStream.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (localStream is not null)
            {
                try
                {
                    await localStream.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            bridgeCts?.Dispose();
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
