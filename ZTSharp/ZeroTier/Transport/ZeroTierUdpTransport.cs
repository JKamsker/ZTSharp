using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.Transport.Internal;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.ZeroTier.Transport;

internal sealed class ZeroTierUdpTransport : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly Channel<ZeroTierUdpDatagram> _incoming;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiverLoop;
    private long _incomingBackpressureCount;
    private bool _disposed;

    public ZeroTierUdpTransport(int localPort = 0, bool enableIpv6 = true, Action<string>? log = null)
    {
        _log = log;
        _udp = OsUdpSocketFactory.Create(localPort, enableIpv6, Log);

        _incoming = Channel.CreateBounded<ZeroTierUdpDatagram>(new BoundedChannelOptions(capacity: 2048)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true
        });
        _receiverLoop = Task.Run(ProcessReceiveLoopAsync);
    }

    public IPEndPoint LocalEndpoint => UdpEndpointNormalization.Normalize((IPEndPoint)_udp.Client.LocalEndPoint!);

    public long IncomingBackpressureCount => Interlocked.Read(ref _incomingBackpressureCount);

    public ValueTask<ZeroTierUdpDatagram> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _incoming.Reader.ReadAsync(cancellationToken);
    }

    public async ValueTask<ZeroTierUdpDatagram> ReceiveAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await ZeroTierTimeouts
            .RunWithTimeoutAsync(timeout, operation: "UDP receive", _incoming.Reader.ReadAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SendAsync(IPEndPoint remoteEndpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndpoint);
        ObjectDisposedException.ThrowIf(_disposed, this);
        UdpEndpointNormalization.ValidateRemoteEndpoint(remoteEndpoint, nameof(remoteEndpoint));
        return _udp.SendAsync(payload, remoteEndpoint, cancellationToken).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        _udp.Dispose();
        _incoming.Writer.TryComplete();
        _cts.Dispose();

        try
        {
            await _receiverLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task ProcessReceiveLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (SocketException ex)
            {
                Log($"UDP receive failed (SocketException {ex.SocketErrorCode}: {ex.Message}).");
                continue;
            }
            catch (InvalidOperationException ex)
            {
                Log($"UDP receive failed (InvalidOperationException: {ex.Message}).");
                continue;
            }

            if (!_incoming.Writer.TryWrite(new ZeroTierUdpDatagram(
                    UdpEndpointNormalization.Normalize(result.RemoteEndPoint),
                    result.Buffer)))
            {
                try
                {
                    var write = _incoming.Writer.WriteAsync(new ZeroTierUdpDatagram(
                        UdpEndpointNormalization.Normalize(result.RemoteEndPoint),
                        result.Buffer), token);
                    if (!write.IsCompletedSuccessfully)
                    {
                        Interlocked.Increment(ref _incomingBackpressureCount);
                    }

                    await write.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or ChannelClosedException)
                {
                    return;
                }
            }
        }
    }

    private void Log(string message)
    {
        _log?.Invoke(message);
    }
}
