using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ZTSharp.Sockets;

namespace ZTSharp.Http;

/// <summary>
/// HttpClient handler that dials overlay TCP streams (not OS TCP) using <see cref="OverlayTcpClient"/>.
/// </summary>
public sealed class OverlayHttpMessageHandler : DelegatingHandler
{
    private readonly Node _node;
    private readonly ulong _networkId;
    private readonly OverlayHttpMessageHandlerOptions _options;
    private int _nextLocalPort;
    private readonly object _localPortLock = new();
    private readonly HashSet<int> _reservedLocalPorts = new();

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
        Justification = "Ownership transfers to OwnedOverlayTcpClientStream, which disposes the client when the HTTP connection is closed.")]
    private async ValueTask<Stream> ConnectOverlayAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.DnsEndPoint;
        var host = GetOriginalHostOrFallback(context, endpoint.Host);
        var remoteNodeId = ResolveNodeId(host);
        var localPort = await AllocateReservedLocalPortAsync(cancellationToken).ConfigureAwait(false);

        var client = new OverlayTcpClient(_node, _networkId, localPort);
        try
        {
            await client.ConnectAsync(remoteNodeId, endpoint.Port, cancellationToken).ConfigureAwait(false);
            return new OwnedOverlayTcpClientStream(client, onDispose: () => ReleaseLocalPort(localPort));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            ReleaseLocalPort(localPort);
            throw;
        }
        catch (Exception ex) when (ex is not HttpRequestException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            ReleaseLocalPort(localPort);
            throw new HttpRequestException(
                $"Overlay connect to '{host}:{endpoint.Port}' failed.",
                ex);
        }
    }

    private async Task<int> AllocateReservedLocalPortAsync(CancellationToken cancellationToken)
    {
        var start = _options.LocalPortStart;
        var end = _options.LocalPortEnd;
        var range = end - start + 1;
        var backoffMs = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < range; i++)
            {
                var candidate = AllocateLocalPort();
                if (TryReserveLocalPort(candidate))
                {
                    return candidate;
                }
            }

            await Task.Delay(backoffMs, cancellationToken).ConfigureAwait(false);
            backoffMs = Math.Min(backoffMs * 2, 50);
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

    private bool TryReserveLocalPort(int port)
    {
        lock (_localPortLock)
        {
            return _reservedLocalPorts.Add(port);
        }
    }

    private void ReleaseLocalPort(int port)
    {
        lock (_localPortLock)
        {
            _reservedLocalPorts.Remove(port);
        }
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

        if (NodeId.TryParse(host, out var parsed))
        {
            return parsed.Value;
        }

        throw new HttpRequestException($"Could not resolve host '{host}' to a managed node id.");
    }

    private static string GetOriginalHostOrFallback(SocketsHttpConnectionContext context, string fallbackHost)
    {
        if (context.InitialRequestMessage?.RequestUri is not { } requestUri)
        {
            return fallbackHost;
        }

        var original = requestUri.OriginalString;
        if (string.IsNullOrWhiteSpace(original))
        {
            return fallbackHost;
        }

        return TryGetHostFromUriString(original, out var host) ? host : fallbackHost;
    }

    private static bool TryGetHostFromUriString(string uri, out string host)
    {
        host = string.Empty;

        var schemeTerminator = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeTerminator < 0)
        {
            return false;
        }

        var authorityStart = schemeTerminator + 3;
        if (authorityStart >= uri.Length)
        {
            return false;
        }

        var authorityEnd = uri.Length;
        for (var i = authorityStart; i < uri.Length; i++)
        {
            var c = uri[i];
            if (c is '/' or '?' or '#')
            {
                authorityEnd = i;
                break;
            }
        }

        var hostStart = authorityStart;
        for (var i = authorityStart; i < authorityEnd; i++)
        {
            if (uri[i] == '@')
            {
                hostStart = i + 1;
                break;
            }
        }

        if (hostStart >= authorityEnd)
        {
            return false;
        }

        if (uri[hostStart] == '[')
        {
            var closingBracket = uri.IndexOf(']', hostStart + 1);
            if (closingBracket < 0 || closingBracket >= authorityEnd)
            {
                return false;
            }

            host = uri.Substring(hostStart + 1, closingBracket - hostStart - 1);
            return host.Length != 0;
        }

        var hostEnd = authorityEnd;
        for (var i = hostStart; i < authorityEnd; i++)
        {
            if (uri[i] == ':')
            {
                hostEnd = i;
                break;
            }
        }

        if (hostEnd <= hostStart)
        {
            return false;
        }

        host = uri.Substring(hostStart, hostEnd - hostStart);
        return host.Length != 0;
    }
}
