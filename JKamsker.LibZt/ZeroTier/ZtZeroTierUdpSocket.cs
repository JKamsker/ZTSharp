using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Internal;
using JKamsker.LibZt.ZeroTier.Net;

namespace JKamsker.LibZt.ZeroTier;

public sealed class ZtZeroTierUdpSocket : IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly Channel<ZtZeroTierRoutedIpv4Packet> _incoming = Channel.CreateUnbounded<ZtZeroTierRoutedIpv4Packet>();
    private readonly ZtZeroTierDataplaneRuntime _runtime;
    private readonly IPAddress _localAddress;
    private readonly ushort _localPort;
    private bool _disposed;

    internal ZtZeroTierUdpSocket(ZtZeroTierDataplaneRuntime runtime, IPAddress localAddress, ushort localPort)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(localAddress);

        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("Only IPv4 is supported in the UDP MVP.");
        }

        if (localPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort), localPort, "Port must be between 1 and 65535.");
        }

        _runtime = runtime;
        _localAddress = localAddress;
        _localPort = localPort;

        if (!_runtime.TryRegisterUdpPort(localPort, _incoming.Writer))
        {
            throw new InvalidOperationException($"A UDP socket is already bound to port {localPort}.");
        }
    }

    public IPEndPoint LocalEndpoint => new(_localAddress, _localPort);

    public async ValueTask<int> SendToAsync(
        ReadOnlyMemory<byte> buffer,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (remoteEndPoint.Port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(remoteEndPoint), remoteEndPoint.Port, "Remote port must be between 1 and 65535.");
        }

        if (remoteEndPoint.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("Only IPv4 is supported in the UDP MVP.");
        }

        var remoteNodeId = await _runtime.ResolveNodeIdAsync(remoteEndPoint.Address, cancellationToken).ConfigureAwait(false);

        var udp = ZtUdpCodec.Encode(
            _localAddress,
            remoteEndPoint.Address,
            sourcePort: _localPort,
            destinationPort: (ushort)remoteEndPoint.Port,
            buffer.Span);

        var ip = ZtIpv4Codec.Encode(
            _localAddress,
            remoteEndPoint.Address,
            protocol: ZtUdpCodec.ProtocolNumber,
            payload: udp,
            identification: GenerateIpIdentification());

        await _runtime.SendIpv4Async(remoteNodeId, ip, cancellationToken).ConfigureAwait(false);
        return buffer.Length;
    }

    public async ValueTask<ZtZeroTierUdpReceiveResult> ReceiveFromAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            ZtZeroTierRoutedIpv4Packet routed;
            try
            {
                routed = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new ObjectDisposedException(nameof(ZtZeroTierUdpSocket));
            }

            if (!ZtIpv4Codec.TryParse(routed.Packet.Span, out var src, out var dst, out var protocol, out var ipPayload))
            {
                continue;
            }

            if (!dst.Equals(_localAddress) || protocol != ZtUdpCodec.ProtocolNumber)
            {
                continue;
            }

            if (!ZtUdpCodec.TryParse(ipPayload, out var srcPort, out var dstPort, out var udpPayload))
            {
                continue;
            }

            if (dstPort != _localPort)
            {
                continue;
            }

            var toCopy = Math.Min(buffer.Length, udpPayload.Length);
            udpPayload.Slice(0, toCopy).CopyTo(buffer.Span);
            return new ZtZeroTierUdpReceiveResult(toCopy, new IPEndPoint(src, srcPort));
        }
    }

    public async ValueTask<ZtZeroTierUdpReceiveResult> ReceiveFromAsync(
        Memory<byte> buffer,
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
            return await ReceiveFromAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"UDP receive timed out after {timeout}.");
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
            _runtime.UnregisterUdpPort(_localPort);
            _incoming.Writer.TryComplete();
        }
        finally
        {
            _disposeLock.Release();
            _disposeLock.Dispose();
        }
    }

    private static ushort GenerateIpIdentification()
    {
        Span<byte> buffer = stackalloc byte[2];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }
}

public readonly record struct ZtZeroTierUdpReceiveResult(int ReceivedBytes, IPEndPoint RemoteEndPoint);
