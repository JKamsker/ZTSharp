using System.Net;
using System.Net.Sockets;

namespace JKamsker.LibZt.Sockets;

/// <summary>
/// Managed UDP client wrapper.
/// </summary>
public sealed class ZtUdpClient : IAsyncDisposable
{
    private readonly UdpClient _client;

    public ZtUdpClient()
    {
        _client = new UdpClient();
    }

    public Task ConnectAsync(string hostname, int port)
        => _client.ConnectAsync(hostname, port);

    public Task SendAsync(byte[] datagram, string hostname, int port, CancellationToken cancellationToken = default)
        => _client.SendAsync(datagram, datagram.Length, hostname, port, cancellationToken).AsTask();

    public Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default)
        => _client.ReceiveAsync(cancellationToken);

    public void Close()
    {
        _client.Close();
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
