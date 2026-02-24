using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ZTSharp.ZeroTier.Transport;

internal sealed class ZeroTierUdpTransport : IAsyncDisposable
{
    private const int WindowsSioUdpConnReset = unchecked((int)0x9800000C);

    private readonly UdpClient _udp;
    private readonly Channel<ZeroTierUdpDatagram> _incoming;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiverLoop;
    private bool _disposed;

    public ZeroTierUdpTransport(int localPort = 0, bool enableIpv6 = true, Action<string>? log = null)
    {
        _log = log;
        _udp = CreateSocket(localPort, enableIpv6);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                _udp.Client.IOControl((IOControlCode)WindowsSioUdpConnReset, [0], null);
            }
            catch (SocketException)
            {
                Log("Failed to disable UDP connection reset handling (SocketException).");
            }
            catch (PlatformNotSupportedException)
            {
                Log("Failed to disable UDP connection reset handling (PlatformNotSupportedException).");
            }
            catch (NotSupportedException)
            {
                Log("Failed to disable UDP connection reset handling (NotSupportedException).");
            }
            catch (ObjectDisposedException)
            {
                Log("Failed to disable UDP connection reset handling (ObjectDisposedException).");
            }
            catch (InvalidOperationException)
            {
                Log("Failed to disable UDP connection reset handling (InvalidOperationException).");
            }
        }

        _incoming = Channel.CreateUnbounded<ZeroTierUdpDatagram>();
        _receiverLoop = Task.Run(ProcessReceiveLoopAsync);
    }

    public IPEndPoint LocalEndpoint => NormalizeEndpointForLocalDelivery((IPEndPoint)_udp.Client.LocalEndPoint!);

    public ValueTask<ZeroTierUdpDatagram> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _incoming.Reader.ReadAsync(cancellationToken);
    }

    public async ValueTask<ZeroTierUdpDatagram> ReceiveAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be greater than zero.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await _incoming.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"UDP receive timed out after {timeout}.");
        }
    }

    public Task SendAsync(IPEndPoint remoteEndpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndpoint);
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
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
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
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
                    NormalizeEndpointForRemoteDelivery(result.RemoteEndPoint),
                    result.Buffer)))
            {
                return;
            }
        }
    }

    private static UdpClient CreateSocket(int localPort, bool enableIpv6)
    {
        if (!enableIpv6)
        {
            var udp4 = new UdpClient(AddressFamily.InterNetwork);
            udp4.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
            return udp4;
        }

        try
        {
            var udp6 = new UdpClient(AddressFamily.InterNetworkV6);
            udp6.Client.DualMode = true;
            udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, localPort));
            return udp6;
        }
        catch (SocketException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (NotSupportedException)
        {
        }

        var udpFallback = new UdpClient(AddressFamily.InterNetwork);
        udpFallback.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
        return udpFallback;
    }

    private static IPEndPoint NormalizeEndpointForLocalDelivery(IPEndPoint endpoint)
    {
        if (endpoint.Address.Equals(IPAddress.Any))
        {
            return new IPEndPoint(IPAddress.Loopback, endpoint.Port);
        }

        if (endpoint.Address.Equals(IPAddress.IPv6Any))
        {
            return new IPEndPoint(IPAddress.IPv6Loopback, endpoint.Port);
        }

        return endpoint;
    }

    private static IPEndPoint NormalizeEndpointForRemoteDelivery(IPEndPoint endpoint)
    {
        if (endpoint.Address.Equals(IPAddress.Any))
        {
            return new IPEndPoint(IPAddress.Loopback, endpoint.Port);
        }

        if (endpoint.Address.Equals(IPAddress.IPv6Any))
        {
            return new IPEndPoint(IPAddress.IPv6Loopback, endpoint.Port);
        }

        return endpoint;
    }

    private void Log(string message)
    {
        _log?.Invoke(message);
    }
}
