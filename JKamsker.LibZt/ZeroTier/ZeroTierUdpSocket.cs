using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Internal;
using JKamsker.LibZt.ZeroTier.Net;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.ZeroTier;

public sealed class ZeroTierUdpSocket : IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly Channel<ZeroTierRoutedIpPacket> _incoming = Channel.CreateUnbounded<ZeroTierRoutedIpPacket>();
    private readonly ZeroTierDataplaneRuntime _runtime;
    private readonly IPAddress _localAddress;
    private readonly ushort _localPort;
    private bool _disposed;

    internal ZeroTierUdpSocket(ZeroTierDataplaneRuntime runtime, IPAddress localAddress, ushort localPort)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(localAddress);

        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException("Only IPv4 and IPv6 are supported.");
        }

        if (localPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort), localPort, "Port must be between 1 and 65535.");
        }

        _runtime = runtime;
        _localAddress = localAddress;
        _localPort = localPort;

        if (!_runtime.TryRegisterUdpPort(localAddress.AddressFamily, localPort, _incoming.Writer))
        {
            throw new InvalidOperationException($"A UDP socket is already bound to {localAddress.AddressFamily} port {localPort}.");
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

        if (remoteEndPoint.Address.AddressFamily != _localAddress.AddressFamily)
        {
            throw new NotSupportedException("Remote address family must match the local binding.");
        }

        var remoteNodeId = await _runtime.ResolveNodeIdAsync(remoteEndPoint.Address, cancellationToken).ConfigureAwait(false);

        var udp = UdpCodec.Encode(
            _localAddress,
            remoteEndPoint.Address,
            sourcePort: _localPort,
            destinationPort: (ushort)remoteEndPoint.Port,
            buffer.Span);

        if (_localAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var ip = Ipv4Codec.Encode(
                _localAddress,
                remoteEndPoint.Address,
                protocol: UdpCodec.ProtocolNumber,
                payload: udp,
                identification: GenerateIpIdentification());

            await _runtime.SendIpv4Async(remoteNodeId, ip, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var ip = Ipv6Codec.Encode(
                _localAddress,
                remoteEndPoint.Address,
                nextHeader: UdpCodec.ProtocolNumber,
                udp,
                hopLimit: 64);

            await _runtime.SendEthernetFrameAsync(remoteNodeId, ZeroTierFrameCodec.EtherTypeIpv6, ip, cancellationToken).ConfigureAwait(false);
        }

        return buffer.Length;
    }

    public async ValueTask<ZeroTierUdpReceiveResult> ReceiveFromAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            ZeroTierRoutedIpPacket routed;
            try
            {
                routed = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new ObjectDisposedException(nameof(ZeroTierUdpSocket));
            }

            IPAddress src;
            IPAddress dst;
            ReadOnlySpan<byte> ipPayload;
            ushort srcPort;
            ushort dstPort;
            ReadOnlySpan<byte> udpPayload;

            if (_localAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (!Ipv4Codec.TryParse(routed.Packet.Span, out src, out dst, out var protocol, out ipPayload))
                {
                    continue;
                }

                if (!dst.Equals(_localAddress) || protocol != UdpCodec.ProtocolNumber)
                {
                    continue;
                }
            }
            else
            {
                if (!Ipv6Codec.TryParse(routed.Packet.Span, out src, out dst, out var nextHeader, out _, out ipPayload))
                {
                    continue;
                }

                if (!dst.Equals(_localAddress) || nextHeader != UdpCodec.ProtocolNumber)
                {
                    continue;
                }
            }

            if (!UdpCodec.TryParse(ipPayload, out srcPort, out dstPort, out udpPayload))
            {
                continue;
            }

            if (dstPort != _localPort)
            {
                continue;
            }

            var toCopy = Math.Min(buffer.Length, udpPayload.Length);
            udpPayload.Slice(0, toCopy).CopyTo(buffer.Span);
            return new ZeroTierUdpReceiveResult(toCopy, new IPEndPoint(src, srcPort));
        }
    }

    public async ValueTask<ZeroTierUdpReceiveResult> ReceiveFromAsync(
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
            _runtime.UnregisterUdpPort(_localAddress.AddressFamily, _localPort);
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

public readonly record struct ZeroTierUdpReceiveResult(int ReceivedBytes, IPEndPoint RemoteEndPoint);
