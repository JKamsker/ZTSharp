using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ZTSharp.ZeroTier.Http;

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
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = context.DnsEndPoint;
        if (IPAddress.TryParse(endpoint.Host, out var ip))
        {
            return await _socket
                .ConnectTcpAsync(new IPEndPoint(ip, endpoint.Port), cancellationToken)
                .ConfigureAwait(false);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SocketException ex)
        {
            throw new HttpRequestException($"Failed to resolve host '{endpoint.Host}'.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new HttpRequestException($"Failed to resolve host '{endpoint.Host}'.", ex);
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"Host '{endpoint.Host}' resolved to no addresses.");
        }

        Exception? lastException = null;
        foreach (var address in addresses)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork &&
                address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                continue;
            }

            try
            {
                return await _socket
                    .ConnectTcpAsync(new IPEndPoint(address, endpoint.Port), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SocketException ex)
            {
                lastException = ex;
            }
            catch (IOException ex)
            {
                lastException = ex;
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
            }
            catch (InvalidOperationException ex)
            {
                lastException = ex;
            }
            catch (NotSupportedException ex)
            {
                lastException = ex;
            }
        }

        throw new HttpRequestException($"Failed to connect to '{endpoint.Host}:{endpoint.Port}'.", lastException);
    }
}
