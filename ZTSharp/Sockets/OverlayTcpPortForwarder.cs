using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using ZTSharp.Internal;
using SystemTcpClient = System.Net.Sockets.TcpClient;

namespace ZTSharp.Sockets;

/// <summary>
/// Simple TCP port forwarder that accepts overlay TCP connections and forwards them to a local OS TCP endpoint.
/// </summary>
public sealed class OverlayTcpPortForwarder : IAsyncDisposable
{
    [SuppressMessage(
        "Reliability",
        "CA2213:Disposable fields should be disposed",
        Justification = "DisposeAsync must be idempotent; disposing this lock can throw on subsequent/overlapping DisposeAsync calls.")]
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ActiveTaskSet _connectionTasks = new();
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

            _connectionTasks.Track(HandleConnectionAsync(accepted, token));
        }

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        drainCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _connectionTasks.WaitAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (drainCts.IsCancellationRequested)
        {
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

            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _connectionTasks.WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (drainCts.IsCancellationRequested)
            {
            }
        }
        finally
        {
            _disposeLock.Release();
            _shutdown.Dispose();
        }
    }

    private async Task HandleConnectionAsync(OverlayTcpClient accepted, CancellationToken cancellationToken)
    {
        var overlayClient = accepted;
        using var localClient = new SystemTcpClient { NoDelay = true };

        using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Stream? localStream = null;
        Stream? overlayStream = null;

        try
        {
            await localClient.ConnectAsync(_targetHost, _targetPort, cancellationToken).ConfigureAwait(false);

            localStream = localClient.GetStream();
            overlayStream = overlayClient.GetStream();
            await BridgeStreamsAsync(overlayStream, localStream, bridgeCts).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (SocketException)
        {
            return;
        }
        finally
        {
            if (overlayStream is not null)
            {
                await DisposeStreamQuietlyAsync(overlayStream).ConfigureAwait(false);
            }

            if (localStream is not null)
            {
                await DisposeStreamQuietlyAsync(localStream).ConfigureAwait(false);
            }

            await overlayClient.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task BridgeStreamsAsync(Stream overlayStream, Stream localStream, CancellationTokenSource bridgeCts)
    {
        var token = bridgeCts.Token;
        var overlayToLocal = CopyAsync(overlayStream, localStream, token);
        var localToOverlay = CopyAsync(localStream, overlayStream, token);

        _ = await Task.WhenAny(overlayToLocal, localToOverlay).ConfigureAwait(false);
        await bridgeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(overlayToLocal, localToOverlay).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static async ValueTask DisposeStreamQuietlyAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }
}
