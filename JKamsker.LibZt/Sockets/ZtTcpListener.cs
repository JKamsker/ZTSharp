using System.Net;
using System.Net.Sockets;
using SystemTcpClient = System.Net.Sockets.TcpClient;
using SystemTcpListener = System.Net.Sockets.TcpListener;

namespace JKamsker.LibZt.Sockets;

/// <summary>
/// Managed TCP listener wrapper.
/// </summary>
public sealed class ZtTcpListener : IAsyncDisposable
{
    private readonly SystemTcpListener _listener;
    private bool _started;

    public ZtTcpListener(IPAddress address, int port)
    {
        _listener = new SystemTcpListener(address, port);
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

#pragma warning disable CA2000 // Wrapper takes ownership of the accepted socket and disposes it.
        SystemTcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2000
        return new ZtTcpClient(client);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _listener.Stop();
        _listener.Dispose();
    }
}
