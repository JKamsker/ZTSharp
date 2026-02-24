using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace JKamsker.LibZt.ZeroTier.Http;

public sealed class ZeroTierHttpMessageHandler : DelegatingHandler
{
    private readonly ZeroTier.ZeroTierSocket _socket;

    public ZeroTierHttpMessageHandler(ZeroTier.ZeroTierSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;

        var sockets = new SocketsHttpHandler
        {
            UseProxy = false
        };

        sockets.ConnectCallback = ConnectAsync;
        InnerHandler = sockets;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.DnsEndPoint;
        if (!IPAddress.TryParse(endpoint.Host, out var ip))
        {
            throw new HttpRequestException($"ZeroTier handler only supports IP hosts in MVP (got '{endpoint.Host}').");
        }

        return await _socket
            .ConnectTcpAsync(new IPEndPoint(ip, endpoint.Port), cancellationToken)
            .ConfigureAwait(false);
    }
}

