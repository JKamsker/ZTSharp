using System.Net;
using System.Net.Sockets;

namespace JKamsker.LibZt.Sockets;

/// <summary>
/// Managed TCP listener wrapper.
/// </summary>
public sealed class ZtTcpListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private bool _started;

    public ZtTcpListener(IPAddress address, int port)
    {
        _listener = new TcpListener(address, port);
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start(int backlog = 100)
    {
        _listener.Start(backlog);
        _started = true;
    }

    public void Stop() => _listener.Stop();

    public async Task<ZtTcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            _listener.Start();
            _started = true;
        }

        var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        return new ZtTcpClient(client);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _listener.Stop();
    }
}
