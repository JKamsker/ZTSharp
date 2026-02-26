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

    [Fact]
    public async Task InMemoryOverlayTcp_AllowsConstruction_BeforeNodeStart()
    {
        var networkId = 0xCAFE0002UL;

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

        await using var listener = new OverlayTcpListener(serverNode, networkId, 20010);
        var acceptTask = listener.AcceptTcpClientAsync().AsTask();

        await using var client = new OverlayTcpClient(clientNode, networkId, 20011);

        await serverNode.StartAsync();
        await clientNode.StartAsync();
        await serverNode.JoinNetworkAsync(networkId);
        await clientNode.JoinNetworkAsync(networkId);

        await client.ConnectAsync(serverNode.NodeId.Value, 20010);

        await using var serverConnection = await acceptTask.WaitAsync(TimeSpan.FromSeconds(2));

        var request = Encoding.UTF8.GetBytes("hello");

        var clientStream = client.GetStream();
        var serverStream = serverConnection.GetStream();

        await clientStream.WriteAsync(request);
        var serverBuffer = new byte[request.Length];
        var serverRead = await StreamTestHelpers.ReadExactAsync(serverStream, serverBuffer, request.Length, CancellationToken.None);
        Assert.Equal(request.Length, serverRead);
        Assert.True(serverBuffer.AsSpan().SequenceEqual(request));
    }

    [Fact]
    public async Task InMemoryOverlayTcp_BuffersData_BeforeConnected()
    {
        var networkId = 0xCAFE0003UL;

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

        var clientPort = 20021;
        var serverPort = 20020;

        await using var client = new OverlayTcpClient(clientNode, networkId, clientPort);

        var synInfoTcs = new TaskCompletionSource<(ulong ConnectionId, int SourcePort)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaptureSyn(in RawFrame frame)
        {
            if (frame.NetworkId != networkId || frame.Payload.Length < OverlayTcpFrameCodec.HeaderLength)
            {
                return;
            }

            if (!OverlayTcpFrameCodec.TryParseHeader(frame.Payload.Span, out var type, out var sourcePort, out var destinationPort, out var destinationNodeId, out var connectionId))
            {
                return;
            }

            if (type != OverlayTcpFrameCodec.FrameType.Syn ||
                destinationPort != serverPort ||
                destinationNodeId != serverNode.NodeId.Value)
            {
                return;
            }

            synInfoTcs.TrySetResult((connectionId, sourcePort));
        }

        serverNode.RawFrameReceived += CaptureSyn;
        try
        {
            var connectTask = client.ConnectAsync(serverNode.NodeId.Value, serverPort);

            var (connectionId, sourcePort) = await synInfoTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var earlyPayload = Encoding.UTF8.GetBytes("early-data");

            var dataFrame = new byte[OverlayTcpFrameCodec.HeaderLength + earlyPayload.Length];
            OverlayTcpFrameCodec.BuildHeader(
                OverlayTcpFrameCodec.FrameType.Data,
                sourcePort: serverPort,
                destinationPort: clientPort,
                destinationNodeId: clientNode.NodeId.Value,
                connectionId,
                dataFrame);
            earlyPayload.CopyTo(dataFrame.AsSpan(OverlayTcpFrameCodec.HeaderLength));
            await serverNode.SendFrameAsync(networkId, dataFrame);

            var synAckFrame = new byte[OverlayTcpFrameCodec.HeaderLength];
            OverlayTcpFrameCodec.BuildHeader(
                OverlayTcpFrameCodec.FrameType.SynAck,
                sourcePort: serverPort,
                destinationPort: clientPort,
                destinationNodeId: clientNode.NodeId.Value,
                connectionId,
                synAckFrame);
            await serverNode.SendFrameAsync(networkId, synAckFrame);

            await connectTask.WaitAsync(TimeSpan.FromSeconds(2));

            var clientStream = client.GetStream();
            var buffer = new byte[earlyPayload.Length];
            var read = await StreamTestHelpers.ReadExactAsync(clientStream, buffer, buffer.Length, CancellationToken.None);
            Assert.Equal(buffer.Length, read);
            Assert.True(buffer.AsSpan().SequenceEqual(earlyPayload));
        }
        finally
        {
            serverNode.RawFrameReceived -= CaptureSyn;
        }
    }
}
