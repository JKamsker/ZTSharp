using System.Net;
using System.Text;
using ZTSharp.Sockets;

namespace ZTSharp.Tests;

public sealed class ZtTcpListenerTests
{
    [Fact]
    public async Task TcpListener_EchoesPayloadOffline()
    {
        await using var listener = new ZtTcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = listener.LocalEndpoint.Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var client = new ZtTcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var server = await acceptTask;

        var request = Encoding.UTF8.GetBytes("ping");
        var stream = client.GetStream();
        await stream.WriteAsync(request, 0, request.Length);

        var serverStream = server.GetStream();
        var buffer = new byte[request.Length];
        var read = await serverStream.ReadAsync(buffer.AsMemory(0, request.Length));
        Assert.Equal(request.Length, read);

        await serverStream.WriteAsync(buffer.AsMemory(0, read));
        var response = new byte[request.Length];
        var responseRead = await stream.ReadAsync(response.AsMemory(0, request.Length));
        Assert.Equal(request.Length, responseRead);
        Assert.Equal(request, response);
    }
}

