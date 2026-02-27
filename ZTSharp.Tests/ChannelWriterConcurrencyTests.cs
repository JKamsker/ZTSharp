using System.Buffers.Binary;
using System.Text;
using ZTSharp.Sockets;

namespace ZTSharp.Tests;

public sealed class ChannelWriterConcurrencyTests
{
    [Fact]
    public async Task InMemoryZtUdpClient_ReceivesDatagrams_UnderConcurrentSends()
    {
        var networkId = 0xCAFE2001UL;

        await using var receiverNode = CreateInMemoryNode();
        await using var senderNode = CreateInMemoryNode();

        await receiverNode.StartAsync();
        await senderNode.StartAsync();
        await receiverNode.JoinNetworkAsync(networkId);
        await senderNode.JoinNetworkAsync(networkId);

        await using var receiver = new ZtUdpClient(receiverNode, networkId, localPort: 30000);
        await using var sender = new ZtUdpClient(senderNode, networkId, localPort: 30001);

        var receiverId = receiverNode.NodeId.Value;
        Assert.NotEqual(0UL, receiverId);

        const int count = 200;
        var sendTasks = new Task[count];
        for (var i = 0; i < count; i++)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(payload, i);
            sendTasks[i] = sender.SendToAsync(payload, receiverId, remotePort: 30000);
        }

        await Task.WhenAll(sendTasks).WaitAsync(TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var datagram = await receiver.ReceiveAsync(cts.Token);
            var value = BinaryPrimitives.ReadInt32LittleEndian(datagram.Payload.Span);
            Assert.InRange(value, 0, count - 1);
            Assert.False(seen[value], $"Duplicate datagram {value}.");
            seen[value] = true;
        }

        Assert.All(seen, Assert.True);
    }

    [Fact]
    public async Task InMemoryOverlayTcpListener_AcceptsMultipleConcurrentClients()
    {
        var networkId = 0xCAFE2002UL;

        await using var serverNode = CreateInMemoryNode();
        await using var clientNode = CreateInMemoryNode();

        await serverNode.StartAsync();
        await clientNode.StartAsync();
        await serverNode.JoinNetworkAsync(networkId);
        await clientNode.JoinNetworkAsync(networkId);

        const int serverPort = 31000;
        const int count = 50;

        await using var listener = new OverlayTcpListener(serverNode, networkId, serverPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = Task.Run(async () =>
        {
            var accepted = new List<OverlayTcpClient>(capacity: count);
            for (var i = 0; i < count; i++)
            {
                accepted.Add(await listener.AcceptTcpClientAsync(cts.Token));
            }

            return accepted;
        }, cts.Token);

        var serverNodeId = serverNode.NodeId.Value;
        Assert.NotEqual(0UL, serverNodeId);

        var connectTasks = new Task[count];
        for (var i = 0; i < count; i++)
        {
            var localPort = 31001 + i;
            connectTasks[i] = Task.Run(async () =>
            {
                await using var client = new OverlayTcpClient(clientNode, networkId, localPort);
                await client.ConnectAsync(serverNodeId, serverPort, cts.Token);
            }, cts.Token);
        }

        await Task.WhenAll(connectTasks).WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        var acceptedClients = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.Equal(count, acceptedClients.Count);

        foreach (var accepted in acceptedClients)
        {
            await accepted.DisposeAsync();
        }
    }

    [Fact]
    public async Task InMemoryOverlayTcpIncomingBuffer_HandlesConcurrentFrameDelivery()
    {
        var networkId = 0xCAFE2003UL;

        await using var serverNode = CreateInMemoryNode();
        await using var clientNode = CreateInMemoryNode();

        await serverNode.StartAsync();
        await clientNode.StartAsync();
        await serverNode.JoinNetworkAsync(networkId);
        await clientNode.JoinNetworkAsync(networkId);

        const int serverPort = 32000;
        const int clientPort = 32001;

        await using var listener = new OverlayTcpListener(serverNode, networkId, serverPort);
        var acceptTask = listener.AcceptTcpClientAsync().AsTask();

        var synInfoTcs = new TaskCompletionSource<(ulong ConnectionId, int SourcePort)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaptureSyn(in RawFrame frame)
        {
            if (frame.NetworkId != networkId || frame.Payload.Length < OverlayTcpFrameCodec.HeaderLength)
            {
                return;
            }

            if (!OverlayTcpFrameCodec.TryParseHeader(
                    frame.Payload.Span,
                    out var type,
                    out var sourcePort,
                    out var destinationPort,
                    out var destinationNodeId,
                    out var connectionId))
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
            await using var client = new OverlayTcpClient(clientNode, networkId, clientPort);
            await client.ConnectAsync(serverNode.NodeId.Value, serverPort);

            await using var _ = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));

            var (connectionId, sourcePort) = await synInfoTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

            const int frames = 500;
            var payload = Encoding.ASCII.GetBytes("abcdefgh");

            var sendTasks = new Task[frames];
            for (var i = 0; i < frames; i++)
            {
                var dataFrame = new byte[OverlayTcpFrameCodec.HeaderLength + payload.Length];
                OverlayTcpFrameCodec.BuildHeader(
                    OverlayTcpFrameCodec.FrameType.Data,
                    sourcePort: serverPort,
                    destinationPort: clientPort,
                    destinationNodeId: clientNode.NodeId.Value,
                    connectionId,
                    dataFrame);
                payload.CopyTo(dataFrame.AsSpan(OverlayTcpFrameCodec.HeaderLength));
                sendTasks[i] = serverNode.SendFrameAsync(networkId, dataFrame);
            }

            await Task.WhenAll(sendTasks).WaitAsync(TimeSpan.FromSeconds(20));

            var clientStream = client.GetStream();
            var buffer = new byte[frames * payload.Length];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var read = await StreamTestHelpers.ReadExactAsync(clientStream, buffer, buffer.Length, cts.Token);
            Assert.Equal(buffer.Length, read);
        }
        finally
        {
            serverNode.RawFrameReceived -= CaptureSyn;
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
