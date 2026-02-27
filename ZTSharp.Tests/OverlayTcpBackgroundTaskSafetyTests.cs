using System.Reflection;
using ZTSharp.Sockets;

namespace ZTSharp.Tests;

public sealed class OverlayTcpBackgroundTaskSafetyTests
{
    [Fact]
    public async Task OverlayTcpListener_SendSynAckFailure_DoesNotFaultTask()
    {
        await using var node = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        await using var listener = new OverlayTcpListener(node, networkId: 1, localPort: 12345);

        var method = typeof(OverlayTcpListener).GetMethod(
            "SendSynAckAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(listener, new object[] { 0xABCDUL, 54321, 1UL })!;
        await task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task OverlayTcpClient_DisposeAsync_IsBounded_WhenFinSendFails()
    {
        var networkId = 0xCAFE3001UL;

        await using var serverNode = CreateInMemoryNode();
        await using var clientNode = CreateInMemoryNode();

        await serverNode.StartAsync();
        await clientNode.StartAsync();
        await serverNode.JoinNetworkAsync(networkId);
        await clientNode.JoinNetworkAsync(networkId);

        await using var listener = new OverlayTcpListener(serverNode, networkId, localPort: 33333);
        var acceptTask = listener.AcceptTcpClientAsync().AsTask();

        var client = new OverlayTcpClient(clientNode, networkId, localPort: 33334);
        var disposed = false;
        try
        {
            await client.ConnectAsync(serverNode.NodeId.Value, remotePort: 33333);
            await using var _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(2));

            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await clientNode.StopAsync(stopCts.Token);

            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            disposed = true;
        }
        finally
        {
            if (!disposed)
            {
                await client.DisposeAsync();
            }
        }
    }

    private static Node CreateInMemoryNode()
    {
        return new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });
    }
}
