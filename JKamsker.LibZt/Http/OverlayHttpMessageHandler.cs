using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using JKamsker.LibZt.Sockets;

namespace JKamsker.LibZt.Http;

public sealed class OverlayHttpMessageHandlerOptions
{
    /// <summary>
    /// Optional mapping of IP addresses to node ids.
    /// </summary>
    public OverlayAddressBook? AddressBook { get; init; }

    /// <summary>
    /// Optional custom resolver for mapping the request host to a node id.
    /// When provided, it is consulted before <see cref="AddressBook"/> and the built-in node id parsing.
    /// </summary>
    public Func<string, ulong?>? HostResolver { get; init; }

    public int LocalPortStart { get; init; } = 49152;

    public int LocalPortEnd { get; init; } = 65535;
}

/// <summary>
/// HttpClient handler that dials overlay TCP streams (not OS TCP) using <see cref="OverlayTcpClient"/>.
/// </summary>
public sealed class OverlayHttpMessageHandler : DelegatingHandler
{
    private readonly Node _node;
    private readonly ulong _networkId;
    private readonly OverlayHttpMessageHandlerOptions _options;
    private int _nextLocalPort;

    public OverlayHttpMessageHandler(Node node, ulong networkId, OverlayHttpMessageHandlerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        _node = node;
        _networkId = networkId;
        _options = options ?? new OverlayHttpMessageHandlerOptions();

        if (_options.LocalPortStart is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "LocalPortStart must be in the range 1..65535.");
        }

        if (_options.LocalPortEnd is < 1 or > ushort.MaxValue || _options.LocalPortEnd < _options.LocalPortStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "LocalPortEnd must be in the range 1..65535 and greater than or equal to LocalPortStart.");
        }

        var sockets = new SocketsHttpHandler
        {
            UseProxy = false
        };

        sockets.ConnectCallback = ConnectOverlayAsync;
        InnerHandler = sockets;

        _nextLocalPort = _options.LocalPortStart - 1;
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to OwnedZtOverlayStream, which disposes the client when the HTTP connection is closed.")]
    private async ValueTask<Stream> ConnectOverlayAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.DnsEndPoint;
        var remoteNodeId = ResolveNodeId(endpoint.Host);
        var localPort = AllocateLocalPort();

        var client = new OverlayTcpClient(_node, _networkId, localPort);
        try
        {
            await client.ConnectAsync(remoteNodeId, endpoint.Port, cancellationToken).ConfigureAwait(false);
            return new OwnedZtOverlayStream(client);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private int AllocateLocalPort()
    {
        var start = _options.LocalPortStart;
        var end = _options.LocalPortEnd;
        var range = end - start + 1;
        var next = Interlocked.Increment(ref _nextLocalPort);
        var offset = (int)((uint)next % (uint)range);
        return start + offset;
    }

    private ulong ResolveNodeId(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new HttpRequestException("Host is required to resolve an overlay node id.");
        }

        var custom = _options.HostResolver?.Invoke(host);
        if (custom.HasValue)
        {
            if (custom.Value == 0 || custom.Value > NodeId.MaxValue)
            {
                throw new HttpRequestException($"Custom host resolver returned an invalid node id for '{host}'.");
            }

            return custom.Value;
        }

        if (IPAddress.TryParse(host, out var ip) && _options.AddressBook is not null)
        {
            if (_options.AddressBook.TryResolve(ip, out var nodeId))
            {
                return nodeId;
            }
        }

        if (TryParseNodeId(host, out var parsed))
        {
            return parsed;
        }

        throw new HttpRequestException($"Could not resolve host '{host}' to a managed node id.");
    }

    private static bool TryParseNodeId(string value, out ulong nodeId)
    {
        nodeId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.AsSpan().Trim();
        var hasHexPrefix = false;
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hasHexPrefix = true;
            trimmed = trimmed.Slice(2);
        }

        if (trimmed.Length == 0)
        {
            return false;
        }

        var treatAsHex = hasHexPrefix || trimmed.Length == 10 || ContainsHexLetters(trimmed);
        if (treatAsHex)
        {
            if (!IsHex(trimmed))
            {
                return false;
            }

            if (!ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ||
                parsed == 0 ||
                parsed > NodeId.MaxValue)
            {
                return false;
            }

            nodeId = parsed;
            return true;
        }

        if (!ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDec) ||
            parsedDec == 0 ||
            parsedDec > NodeId.MaxValue)
        {
            return false;
        }

        nodeId = parsedDec;
        return true;
    }

    private static bool ContainsHexLetters(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= 'a' and <= 'f' or >= 'A' and <= 'F')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= '0' and <= '9')
            {
                continue;
            }

            if (c is >= 'a' and <= 'f')
            {
                continue;
            }

            if (c is >= 'A' and <= 'F')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private sealed class OwnedZtOverlayStream : Stream
    {
        private readonly OverlayTcpClient _client;
        private readonly Stream _inner;
        private int _disposed;

        public OwnedZtOverlayStream(OverlayTcpClient client)
        {
            _client = client;
            _inner = client.GetStream();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            if (disposing)
            {
                _inner.Dispose();
                _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _inner.DisposeAsync().ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
