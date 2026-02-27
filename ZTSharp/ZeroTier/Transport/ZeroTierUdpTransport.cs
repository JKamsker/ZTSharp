using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading.Channels;
using ZTSharp.Transport.Internal;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.ZeroTier.Transport;

internal sealed class ZeroTierUdpTransport : IZeroTierUdpTransport
{
    private readonly UdpClient _udp;
    private readonly Channel<ZeroTierUdpDatagram> _incoming;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiverLoop;
    private readonly int _localSocketId;
    private long _incomingBackpressureCount;
    private int _disposed;

    public ZeroTierUdpTransport(int localPort = 0, bool enableIpv6 = true, Action<string>? log = null, int localSocketId = 0)
    {
        _log = log;
        _localSocketId = localSocketId;
        _udp = OsUdpSocketFactory.Create(localPort, enableIpv6, Log);

        _incoming = Channel.CreateBounded<ZeroTierUdpDatagram>(new BoundedChannelOptions(capacity: 2048)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true
        });
        _receiverLoop = Task.Run(ProcessReceiveLoopAsync);
    }

    public int LocalSocketId => _localSocketId;

    public IPEndPoint LocalEndpoint => UdpEndpointNormalization.Normalize((IPEndPoint)_udp.Client.LocalEndPoint!);

    public IReadOnlyList<ZeroTierUdpLocalSocket> LocalSockets
        => new[] { new ZeroTierUdpLocalSocket(_localSocketId, LocalEndpoint) };

    public long IncomingBackpressureCount => Interlocked.Read(ref _incomingBackpressureCount);

    public ValueTask<ZeroTierUdpDatagram> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _incoming.Reader.ReadAsync(cancellationToken);
    }

    public async ValueTask<ZeroTierUdpDatagram> ReceiveAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return await ZeroTierTimeouts
            .RunWithTimeoutAsync(timeout, operation: "UDP receive", _incoming.Reader.ReadAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SendAsync(IPEndPoint remoteEndpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndpoint);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        UdpEndpointNormalization.ValidateRemoteEndpoint(remoteEndpoint, nameof(remoteEndpoint));
        return _udp.SendAsync(payload, remoteEndpoint, cancellationToken).AsTask();
    }

    public Task SendAsync(int localSocketId, IPEndPoint remoteEndpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (localSocketId != _localSocketId)
        {
            throw new ArgumentOutOfRangeException(nameof(localSocketId), localSocketId, $"Invalid local socket id. This transport exposes only id {_localSocketId}.");
        }

        return SendAsync(remoteEndpoint, payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _incoming.Writer.TryComplete();
        try
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Dispose must be best-effort.
            catch (Exception ex)
#pragma warning restore CA1031
            {
#if DEBUG
                Debug.WriteLine($"[{nameof(ZeroTierUdpTransport)}] CancelAsync failed: {ex}");
#else
                _ = ex;
#endif
            }

            try
            {
                _udp.Dispose();
            }
#pragma warning disable CA1031 // Dispose must be best-effort.
            catch (Exception ex)
#pragma warning restore CA1031
            {
#if DEBUG
                Debug.WriteLine($"[{nameof(ZeroTierUdpTransport)}] UdpClient dispose failed: {ex}");
#else
                _ = ex;
#endif
            }
        }
        finally
        {
            _cts.Dispose();
        }

        try
        {
            await _receiverLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
#pragma warning disable CA1031 // Dispose must be best-effort.
        catch (Exception ex)
#pragma warning restore CA1031
        {
#if DEBUG
            Debug.WriteLine($"[{nameof(ZeroTierUdpTransport)}] Receiver loop completion failed: {ex}");
#else
            _ = ex;
#endif
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

            var datagram = new ZeroTierUdpDatagram(
                _localSocketId,
                UdpEndpointNormalization.Normalize(result.RemoteEndPoint),
                result.Buffer);
            if (!_incoming.Writer.TryWrite(datagram))
            {
                Interlocked.Increment(ref _incomingBackpressureCount);
                if (_incoming.Writer.TryWrite(datagram))
                {
                    continue;
                }

                if (_incoming.Reader.TryRead(out _))
                {
                    _incoming.Writer.TryWrite(datagram);
                }
            }
        }
    }

    private void Log(string message)
    {
        _log?.Invoke(message);
    }
}
