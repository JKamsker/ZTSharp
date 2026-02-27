using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.ZeroTier.Http;

public sealed class ZeroTierHttpMessageHandler : DelegatingHandler
{
    private readonly ZeroTier.ZeroTierSocket _socket;
    private static readonly TimeSpan DefaultPerAddressConnectTimeout = TimeSpan.FromSeconds(2);

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
            return await ConnectToResolvedAddressesAsync(
                    endpoint,
                    new[] { ip },
                    connectAsync: (ep, ct) => _socket.ConnectTcpAsync(ep, ct),
                    perAddressConnectTimeout: DefaultPerAddressConnectTimeout,
                    cancellationToken)
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

        return await ConnectToResolvedAddressesAsync(
                endpoint,
                addresses,
                connectAsync: (ep, ct) => _socket.ConnectTcpAsync(ep, ct),
                perAddressConnectTimeout: DefaultPerAddressConnectTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async ValueTask<Stream> ConnectToResolvedAddressesAsync(
        DnsEndPoint endpoint,
        IPAddress[] addresses,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connectAsync,
        TimeSpan perAddressConnectTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(connectAsync);
        cancellationToken.ThrowIfCancellationRequested();

        if (perAddressConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(perAddressConnectTimeout), perAddressConnectTimeout, "Timeout must be greater than zero.");
        }

        Exception? lastException = null;
        for (var i = 0; i < addresses.Length; i++)
        {
            var address = addresses[i];
            if (address.AddressFamily != AddressFamily.InterNetwork &&
                address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                continue;
            }

            try
            {
                return await ZeroTierTimeouts.RunWithTimeoutAsync(
                        perAddressConnectTimeout,
                        operation: $"HTTP connect ({address})",
                        action: ct => connectAsync(new IPEndPoint(address, endpoint.Port), ct),
                        cancellationToken)
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
