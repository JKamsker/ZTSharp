using System.Net;
using System.Diagnostics;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.ZeroTier.Transport;

internal sealed class ZeroTierUdpMultiTransport : IZeroTierUdpTransport
{
    private readonly IReadOnlyList<ZeroTierUdpTransport> _sockets;
    private readonly Channel<ZeroTierUdpDatagram> _incoming;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _forwarders;
    private int _disposed;

    public ZeroTierUdpMultiTransport(IReadOnlyList<ZeroTierUdpTransport> sockets)
    {
        ArgumentNullException.ThrowIfNull(sockets);
        if (sockets.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sockets), "At least one UDP socket is required.");
        }

        _sockets = sockets;
        _incoming = Channel.CreateBounded<ZeroTierUdpDatagram>(new BoundedChannelOptions(capacity: 2048)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _forwarders = new Task[_sockets.Count];
        for (var i = 0; i < _sockets.Count; i++)
        {
            var socket = _sockets[i];
            _forwarders[i] = Task.Run(() => ForwardLoopAsync(socket, _cts.Token), CancellationToken.None);
        }
    }

    public IReadOnlyList<ZeroTierUdpLocalSocket> LocalSockets
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _sockets.Select(socket => new ZeroTierUdpLocalSocket(socket.LocalSocketId, socket.LocalEndpoint)).ToArray();
        }
    }

    public ValueTask<ZeroTierUdpDatagram> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _incoming.Reader.ReadAsync(cancellationToken);
    }

    public async ValueTask<ZeroTierUdpDatagram> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return await ZeroTierTimeouts
            .RunWithTimeoutAsync(timeout, operation: "UDP receive", _incoming.Reader.ReadAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SendAsync(IPEndPoint remoteEndpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _sockets[0].SendAsync(remoteEndpoint, payload, cancellationToken);
    }

    public Task SendAsync(int localSocketId, IPEndPoint remoteEndpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var socket = GetSocket(localSocketId);
        return socket.SendAsync(remoteEndpoint, payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _incoming.Writer.TryComplete();

        var forwarderCompletion = Task.WhenAll(_forwarders);
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
                Debug.WriteLine($"[{nameof(ZeroTierUdpMultiTransport)}] CancelAsync failed: {ex}");
#else
                _ = ex;
#endif
            }

            foreach (var socket in _sockets)
            {
                try
                {
                    await socket.DisposeAsync().ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Dispose must be best-effort.
                catch (Exception ex)
#pragma warning restore CA1031
                {
#if DEBUG
                    Debug.WriteLine($"[{nameof(ZeroTierUdpMultiTransport)}] Socket dispose failed: {ex}");
#else
                    _ = ex;
#endif
                }
            }
        }
        finally
        {
            try
            {
                await forwarderCompletion.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Dispose must be best-effort.
            catch (Exception ex)
#pragma warning restore CA1031
            {
#if DEBUG
                Debug.WriteLine($"[{nameof(ZeroTierUdpMultiTransport)}] Forwarder completion failed: {ex}");
#else
                _ = ex;
#endif
            }

            _cts.Dispose();
        }
    }

    private ZeroTierUdpTransport GetSocket(int localSocketId)
    {
        for (var i = 0; i < _sockets.Count; i++)
        {
            var socket = _sockets[i];
            if (socket.LocalSocketId == localSocketId)
            {
                return socket;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(localSocketId), localSocketId, "Unknown local socket id.");
    }

    private async Task ForwardLoopAsync(ZeroTierUdpTransport socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ZeroTierUdpDatagram datagram;
            try
            {
                datagram = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                await _incoming.Writer.WriteAsync(datagram, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }
}
