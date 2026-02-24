using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Internal;
using JKamsker.LibZt.ZeroTier.Net;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.ZeroTier;

public sealed class ZtZeroTierTcpListener : IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<Task> _connectionTasks = new();
    private readonly Channel<Stream> _acceptQueue = Channel.CreateUnbounded<Stream>();
    private readonly ZtZeroTierDataplaneRuntime _runtime;
    private readonly IPAddress _localAddress;
    private readonly ushort _localPort;
    private bool _disposed;

    internal ZtZeroTierTcpListener(ZtZeroTierDataplaneRuntime runtime, IPAddress localAddress, ushort localPort)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(localAddress);
        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException("Only IPv4 and IPv6 are supported.");
        }

        _runtime = runtime;
        _localAddress = localAddress;
        _localPort = localPort;

        if (!_runtime.TryRegisterTcpListener(localAddress.AddressFamily, localPort, OnSynAsync))
        {
            throw new InvalidOperationException($"A TCP listener is already registered for {localAddress.AddressFamily} port {localPort}.");
        }
    }

    public IPEndPoint LocalEndpoint => new(_localAddress, _localPort);

    public ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _acceptQueue.Reader.ReadAsync(cancellationToken);
    }

    public async ValueTask<Stream> AcceptAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
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
            return await AcceptAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"TCP accept timed out after {timeout}.");
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
            _runtime.UnregisterTcpListener(_localAddress.AddressFamily, _localPort);
            await _shutdown.CancelAsync().ConfigureAwait(false);
            _acceptQueue.Writer.TryComplete();

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

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of accepted connections transfers to the accept queue consumer (via the returned Stream).")]
    private Task OnSynAsync(ZtNodeId peerNodeId, ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        IPAddress src;
        IPAddress dst;
        ReadOnlySpan<byte> ipPayload;
        if (_localAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (!ZtIpv4Codec.TryParse(ipPacket.Span, out src, out dst, out var protocol, out ipPayload))
            {
                return Task.CompletedTask;
            }

            if (!dst.Equals(_localAddress) || protocol != ZtTcpCodec.ProtocolNumber)
            {
                return Task.CompletedTask;
            }
        }
        else
        {
            if (!ZtIpv6Codec.TryParse(ipPacket.Span, out src, out dst, out var nextHeader, out _, out ipPayload))
            {
                return Task.CompletedTask;
            }

            if (!dst.Equals(_localAddress) || nextHeader != ZtTcpCodec.ProtocolNumber)
            {
                return Task.CompletedTask;
            }
        }

        if (!ZtTcpCodec.TryParse(ipPayload, out var srcPort, out var dstPort, out _, out _, out var flags, out _, out _))
        {
            return Task.CompletedTask;
        }

        if (dstPort != _localPort || (flags & ZtTcpCodec.Flags.Syn) == 0 || (flags & ZtTcpCodec.Flags.Ack) != 0)
        {
            return Task.CompletedTask;
        }

        var remoteEndpoint = new IPEndPoint(src, srcPort);
        var localEndpoint = new IPEndPoint(dst, dstPort);

        var link = _runtime.RegisterTcpRoute(peerNodeId, localEndpoint, remoteEndpoint);
        var connection = new ZtUserSpaceTcpServerConnection(
            link,
            localAddress: dst,
            localPort: dstPort,
            remoteAddress: src,
            remotePort: srcPort);

        link.IncomingWriter.TryWrite(ipPacket);

        _connectionTasks.Add(HandleAcceptedConnectionAsync(connection, cancellationToken));
        return Task.CompletedTask;
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of accepted connections transfers to the accept queue consumer (via the returned Stream).")]
    private async Task HandleAcceptedConnectionAsync(ZtUserSpaceTcpServerConnection connection, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        var token = linkedCts.Token;

        Stream? stream = null;
        var handedOff = false;
        var disposed = false;

        try
        {
            await connection.AcceptAsync(token).ConfigureAwait(false);
            stream = connection.GetStream();

            if (!_acceptQueue.Writer.TryWrite(stream))
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                disposed = true;
                stream = null;
                return;
            }

            handedOff = true;
            stream = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (!handedOff && !disposed)
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
