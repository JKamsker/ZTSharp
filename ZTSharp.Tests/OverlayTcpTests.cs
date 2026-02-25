using System.Text;
using ZTSharp.Sockets;

namespace ZTSharp.Tests;

public sealed class OverlayTcpTests
{
    [Fact]
    public async Task InMemoryOverlayTcp_EchoesPayload()
    {
        var networkId = 0xCAFE0001UL;

        await using var serverNode = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        await using var clientNode = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        await serverNode.StartAsync();
        await clientNode.StartAsync();
        await serverNode.JoinNetworkAsync(networkId);
        await clientNode.JoinNetworkAsync(networkId);

        await using var listener = new OverlayTcpListener(serverNode, networkId, 20000);
        var acceptTask = listener.AcceptTcpClientAsync().AsTask();

        await using var client = new OverlayTcpClient(clientNode, networkId, 20001);
        await client.ConnectAsync(serverNode.NodeId.Value, 20000);

        await using var serverConnection = await acceptTask.WaitAsync(TimeSpan.FromSeconds(2));

        var request = Encoding.UTF8.GetBytes("ping");
        var response = Encoding.UTF8.GetBytes("pong");

        var clientStream = client.GetStream();
        var serverStream = serverConnection.GetStream();

        await clientStream.WriteAsync(request);
        var serverBuffer = new byte[request.Length];
        var serverRead = await StreamTestHelpers.ReadExactAsync(serverStream, serverBuffer, request.Length, CancellationToken.None);
        Assert.Equal(request.Length, serverRead);
        Assert.True(serverBuffer.AsSpan().SequenceEqual(request));

        await serverStream.WriteAsync(response);
        var clientBuffer = new byte[response.Length];
        var clientRead = await StreamTestHelpers.ReadExactAsync(clientStream, clientBuffer, response.Length, CancellationToken.None);
        Assert.Equal(response.Length, clientRead);
        Assert.True(clientBuffer.AsSpan().SequenceEqual(response));
    }
}
