using System.Net;
using System.Net.Sockets;
using SystemTcpClient = System.Net.Sockets.TcpClient;

namespace JKamsker.LibZt.Sockets;

/// <summary>
/// Managed TCP client wrapper with a small API surface in line with ZeroTier-style usage.
/// </summary>
public sealed class ZtTcpClient : IAsyncDisposable
{
    private readonly SystemTcpClient _client;

    internal ZtTcpClient(SystemTcpClient client)
    {
        _client = client;
    }

    public ZtTcpClient()
    {
        _client = new SystemTcpClient();
    }

    public bool Connected => _client.Connected;

    public NetworkStream GetStream() => _client.GetStream();

    public async Task ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConnectAsync(string hostname, int port, CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(hostname, port, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await GetStream().WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.Length;
    }

    public Task CloseAsync()
    {
        _client.Close();
        return Task.CompletedTask;
    }

    public Task<NetworkStream> GetNetworkStreamAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_client.GetStream());

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
