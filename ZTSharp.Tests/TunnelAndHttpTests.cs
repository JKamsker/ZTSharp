using System.Net;
using System.Net.Sockets;
using System.Text;
using ZTSharp.Http;
using ZTSharp.Sockets;
using SystemTcpListener = System.Net.Sockets.TcpListener;

namespace ZTSharp.Tests;

public sealed class TunnelAndHttpTests
{
    [Fact]
    public async Task InMemoryOverlayPortForwarder_ForwardsBytes()
    {
        var networkId = 0xCAFE1001UL;

        await using var serverNode = CreateInMemoryNode();
        await using var clientNode = CreateInMemoryNode();

        await serverNode.StartAsync();
        await clientNode.StartAsync();

        await serverNode.JoinNetworkAsync(networkId);
        await clientNode.JoinNetworkAsync(networkId);

        var echoListener = new SystemTcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        try
        {
            var echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var echoTask = Task.Run(async () =>
            {
                using var tcp = await echoListener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                tcp.NoDelay = true;
                using var stream = tcp.GetStream();

                var buffer = new byte[4];
                var read = await StreamTestHelpers.ReadExactAsync(stream, buffer, buffer.Length, cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
            }, cts.Token);

            await using var forwarder = new OverlayTcpPortForwarder(
                serverNode,
                networkId,
                overlayListenPort: 25000,
                targetHost: "127.0.0.1",
                targetPort: echoPort);

            var forwarderTask = Task.Run(() => forwarder.RunAsync(cts.Token), cts.Token);

            await using var overlayClient = new OverlayTcpClient(clientNode, networkId, localPort: 25001);
            await overlayClient.ConnectAsync(serverNode.NodeId.Value, remotePort: 25000, cts.Token);

            var stream = overlayClient.GetStream();
            await stream.WriteAsync("ping"u8.ToArray(), cts.Token);

            var reply = new byte[4];
            var replyRead = await StreamTestHelpers.ReadExactAsync(stream, reply, reply.Length, cts.Token);
            Assert.Equal(reply.Length, replyRead);
            Assert.True(reply.AsSpan().SequenceEqual("ping"u8));

            cts.Cancel();

            try
            {
                await Task.WhenAll(echoTask, forwarderTask).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
        }
        finally
        {
            echoListener.Stop();
        }
    }

    [Fact]
    public async Task InMemoryOverlayHttpHandler_CanFetchThroughForwarder()
    {
        var networkId = 0xCAFE1002UL;

        await using var serverNode = CreateInMemoryNode();
        await using var clientNode = CreateInMemoryNode();

        await serverNode.StartAsync();
        await clientNode.StartAsync();

        await serverNode.JoinNetworkAsync(networkId);
        await clientNode.JoinNetworkAsync(networkId);

        var httpListener = new SystemTcpListener(IPAddress.Loopback, 0);
        httpListener.Start();
        try
        {
            var localHttpPort = ((IPEndPoint)httpListener.LocalEndpoint).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var httpTask = Task.Run(async () =>
            {
                using var tcp = await httpListener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                tcp.NoDelay = true;
                using var stream = tcp.GetStream();

                var buffer = new byte[4096];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0)
                    {
                        break;
                    }
                }

                var body = "zt-ok";
                var response = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                var responseBytes = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(responseBytes, cts.Token).ConfigureAwait(false);
            }, cts.Token);

            await using var forwarder = new OverlayTcpPortForwarder(
                serverNode,
                networkId,
                overlayListenPort: 28080,
                targetHost: "127.0.0.1",
                targetPort: localHttpPort);

            var forwarderTask = Task.Run(() => forwarder.RunAsync(cts.Token), cts.Token);

            using var httpClient = new HttpClient(new OverlayHttpMessageHandler(clientNode, networkId));
            var uri = new Uri($"http://{serverNode.NodeId.Value:x10}:28080/hello");
            var text = await httpClient.GetStringAsync(uri, cts.Token);
            Assert.Equal("zt-ok", text);

            cts.Cancel();

            try
            {
                await Task.WhenAll(httpTask, forwarderTask).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
        }
        finally
        {
            httpListener.Stop();
        }
    }

    [Fact]
    public async Task InMemoryOverlayHttpHandler_CanResolveIpViaAddressBook()
    {
        var networkId = 0xCAFE1003UL;

        await using var serverNode = CreateInMemoryNode();
        await using var clientNode = CreateInMemoryNode();

        await serverNode.StartAsync();
        await clientNode.StartAsync();

        await serverNode.JoinNetworkAsync(networkId);
        await clientNode.JoinNetworkAsync(networkId);

        var httpListener = new SystemTcpListener(IPAddress.Loopback, 0);
        httpListener.Start();
        try
        {
            var localHttpPort = ((IPEndPoint)httpListener.LocalEndpoint).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var httpTask = Task.Run(async () =>
            {
                using var tcp = await httpListener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                tcp.NoDelay = true;
                using var stream = tcp.GetStream();

                var buffer = new byte[4096];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0)
                    {
                        break;
                    }
                }

                var body = "zt-ip-ok";
                var response = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                var responseBytes = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(responseBytes, cts.Token).ConfigureAwait(false);
            }, cts.Token);

            await using var forwarder = new OverlayTcpPortForwarder(
                serverNode,
                networkId,
                overlayListenPort: 28081,
                targetHost: "127.0.0.1",
                targetPort: localHttpPort);

            var forwarderTask = Task.Run(() => forwarder.RunAsync(cts.Token), cts.Token);

            var addressBook = new OverlayAddressBook();
            addressBook.Add(IPAddress.Parse("10.1.2.3"), serverNode.NodeId.Value);

            var handler = new OverlayHttpMessageHandler(
                clientNode,
                networkId,
                new OverlayHttpMessageHandlerOptions { AddressBook = addressBook });

            using var httpClient = new HttpClient(handler);
            var text = await httpClient.GetStringAsync("http://10.1.2.3:28081/", cts.Token);
            Assert.Equal("zt-ip-ok", text);

            cts.Cancel();

            try
            {
                await Task.WhenAll(httpTask, forwarderTask).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
        }
        finally
        {
            httpListener.Stop();
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
