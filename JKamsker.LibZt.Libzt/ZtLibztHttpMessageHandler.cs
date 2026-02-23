using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using JKamsker.LibZt.Libzt.Sockets;

namespace JKamsker.LibZt.Libzt;

/// <summary>
/// <see cref="HttpClient"/> handler that dials TCP connections over upstream <c>libzt</c>
/// (via <see cref="global::ZeroTier.Sockets.Socket"/>).
/// </summary>
public sealed class ZtLibztHttpMessageHandler : DelegatingHandler
{
    public ZtLibztHttpMessageHandler()
    {
        var sockets = new SocketsHttpHandler
        {
            UseProxy = false
        };

        sockets.ConnectCallback = ConnectLibztAsync;
        InnerHandler = sockets;
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to ZtLibztSocketStream, which closes the socket on dispose.")]
    private static ValueTask<Stream> ConnectLibztAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.DnsEndPoint;
        if (!TryResolveIPAddress(endpoint.Host, out var address))
        {
            throw new HttpRequestException($"Could not resolve host '{endpoint.Host}' to an IP address.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var socket = new global::ZeroTier.Sockets.Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Connect(new IPEndPoint(address, endpoint.Port));
            socket.NoDelay = true;
            Stream stream = new ZtLibztSocketStream(socket, ownsSocket: true);
            return ValueTask.FromResult(stream);
        }
        catch
        {
#pragma warning disable CA1031
            try
            {
                socket.Close();
            }
            catch
            {
            }
#pragma warning restore CA1031
            throw;
        }
    }

    private static bool TryResolveIPAddress(string host, out IPAddress address)
    {
        if (IPAddress.TryParse(host, out var parsed) && parsed is not null)
        {
            address = parsed;
            return true;
        }

        try
        {
            var resolved = Dns.GetHostAddresses(host);
            foreach (var candidate in resolved)
            {
                if (candidate.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                {
                    address = candidate;
                    return true;
                }
            }
        }
        catch (SocketException)
        {
        }

        address = IPAddress.None;
        return false;
    }
}
