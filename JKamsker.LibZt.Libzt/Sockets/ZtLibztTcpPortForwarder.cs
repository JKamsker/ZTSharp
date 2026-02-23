using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace JKamsker.LibZt.Libzt.Sockets;

/// <summary>
/// Simple TCP port forwarder that accepts libzt TCP connections and forwards them to a local OS TCP endpoint.
/// </summary>
public sealed class ZtLibztTcpPortForwarder : IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<Task> _connectionTasks = new();
    private readonly int _listenPort;
    private readonly string _targetHost;
    private readonly int _targetPort;

    private global::ZeroTier.Sockets.Socket? _listener;
    private bool _disposed;

    public ZtLibztTcpPortForwarder(int listenPort, string targetHost, int targetPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        if (listenPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(listenPort));
        }

        if (targetPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPort));
        }

        _listenPort = listenPort;
        _targetHost = targetHost;
        _targetPort = targetPort;
    }

    public int ListenPort => _listenPort;

    public string TargetHost => _targetHost;

    public int TargetPort => _targetPort;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linkedCts.Token;

        var listener = new global::ZeroTier.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(IPAddress.Any, _listenPort));
            listener.Listen(backlog: 256);

            _listener = listener;

            using var _ = token.Register(static state =>
            {
#pragma warning disable CA1031
                try
                {
                    ((global::ZeroTier.Sockets.Socket)state!).Close();
                }
                catch
                {
                }
#pragma warning restore CA1031
            }, listener);

            while (!token.IsCancellationRequested)
            {
                global::ZeroTier.Sockets.Socket accepted;
                try
                {
                    accepted = listener.Accept();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (global::ZeroTier.Sockets.SocketException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (global::ZeroTier.Sockets.SocketException)
                {
                    continue;
                }

                _connectionTasks.Add(HandleConnectionAsync(accepted, token));
            }
        }
        finally
        {
            try
            {
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
            catch (global::ZeroTier.Sockets.SocketException)
            {
            }

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

            try
            {
                _listener?.Close();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
            catch (global::ZeroTier.Sockets.SocketException)
            {
            }

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
            _listener = null;
            _disposeLock.Release();
            _disposeLock.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task HandleConnectionAsync(global::ZeroTier.Sockets.Socket accepted, CancellationToken cancellationToken)
    {
        global::ZeroTier.Sockets.Socket? ztSocket = null;
        ZtLibztSocketStream? ztStream = null;
        TcpClient? localClient = null;
        CancellationTokenSource? bridgeCts = null;
        Task? ztToLocal = null;
        Task? localToZt = null;

        try
        {
            ztSocket = accepted;
            ztSocket.NoDelay = true;

            localClient = new TcpClient { NoDelay = true };
            await localClient.ConnectAsync(_targetHost, _targetPort, cancellationToken).ConfigureAwait(false);

            var localStream = localClient.GetStream();
            ztStream = new ZtLibztSocketStream(ztSocket, ownsSocket: true);
            ztSocket = null;

            bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var bridgeToken = bridgeCts.Token;

            ztToLocal = CopyAsync(ztStream, localStream, bridgeToken);
            localToZt = CopyAsync(localStream, ztStream, bridgeToken);

            _ = await Task.WhenAny(ztToLocal, localToZt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
        }
        catch (global::ZeroTier.Sockets.SocketException)
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

            if (ztToLocal is not null || localToZt is not null)
            {
                try
                {
                    await Task
                        .WhenAll(
                            ztToLocal ?? Task.CompletedTask,
                            localToZt ?? Task.CompletedTask)
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
#pragma warning disable CA1849
            ztStream?.Dispose();
#pragma warning restore CA1849

#pragma warning disable CA1031
            try
            {
                ztSocket?.Close();
            }
            catch
            {
            }
#pragma warning restore CA1031
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
