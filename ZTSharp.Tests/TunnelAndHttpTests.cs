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
            var uri = new Uri($"http://{serverNode.NodeId.ToHexString()}:28080/hello");
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
    public async Task InMemoryOverlayHttpHandler_DisposingResponse_DoesNotThrowOrHang()
    {
        var networkId = 0xCAFE1004UL;

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

                var body = "zt-dispose-ok";
                var response = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                var responseBytes = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(responseBytes, cts.Token).ConfigureAwait(false);
            }, cts.Token);

            await using var forwarder = new OverlayTcpPortForwarder(
                serverNode,
                networkId,
                overlayListenPort: 28082,
                targetHost: "127.0.0.1",
                targetPort: localHttpPort);

            var forwarderTask = Task.Run(() => forwarder.RunAsync(cts.Token), cts.Token);

            using var httpClient = new HttpClient(new OverlayHttpMessageHandler(clientNode, networkId));
            var uri = new Uri($"http://{serverNode.NodeId.ToHexString()}:28082/hello");

            using var responseMessage = await httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, uri),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            await Task.Run(() => responseMessage.Dispose(), cts.Token).WaitAsync(TimeSpan.FromSeconds(2), cts.Token);

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
    public async Task InMemoryOverlayHttpHandler_ConnectTimeout_IsHttpRequestException()
    {
        var networkId = 0xCAFE1005UL;

        await using var serverNode = CreateInMemoryNode();
        await using var clientNode = CreateInMemoryNode();

        await serverNode.StartAsync();
        await clientNode.StartAsync();

        await serverNode.JoinNetworkAsync(networkId);
        await clientNode.JoinNetworkAsync(networkId);

        using var httpClient = new HttpClient(new OverlayHttpMessageHandler(clientNode, networkId));
        var uri = new Uri($"http://{serverNode.NodeId.ToHexString()}:29999/timeout");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => httpClient.GetAsync(uri, cts.Token));

        static bool ContainsTimeout(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is TimeoutException)
                {
                    return true;
                }
            }

            return false;
        }

        Assert.True(ContainsTimeout(ex), "Expected a TimeoutException somewhere in the HttpClient exception chain.");
    }

    [Fact]
    public async Task InMemoryOverlayHttpHandler_LocalPortAllocator_RetriesUnderConcurrency()
    {
        var networkId = 0xCAFE1006UL;

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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var releaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectionTasks = new List<Task>(capacity: 3);

            static async Task HandleConnectionAsync(
                TcpClient tcp,
                Task release,
                CancellationToken cancellationToken)
            {
                using (tcp)
                {
                    tcp.NoDelay = true;
                    await using var stream = tcp.GetStream();

                    var buffer = new byte[4096];
                    var total = 0;
                    while (total < buffer.Length)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
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

                    var bodyLength = 1024;
                    var response = $"HTTP/1.1 200 OK\r\nContent-Length: {bodyLength}\r\nConnection: keep-alive\r\n\r\n";
                    var responseBytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);

                    await release.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var acceptTask = Task.Run(async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var tcp = await httpListener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                    connectionTasks.Add(HandleConnectionAsync(tcp, releaseTcs.Task, cts.Token));
                }
            }, cts.Token);

            await using var forwarder = new OverlayTcpPortForwarder(
                serverNode,
                networkId,
                overlayListenPort: 28083,
                targetHost: "127.0.0.1",
                targetPort: localHttpPort);

            var forwarderTask = Task.Run(() => forwarder.RunAsync(cts.Token), cts.Token);

            var handler = new OverlayHttpMessageHandler(
                clientNode,
                networkId,
                new OverlayHttpMessageHandlerOptions
                {
                    LocalPortStart = 60000,
                    LocalPortEnd = 60001
                });

            using var httpClient = new HttpClient(handler);
            var uri = new Uri($"http://{serverNode.NodeId.ToHexString()}:28083/ports");

            using var response1 = await httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, uri),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            using var response2 = await httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, uri),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            var response3Task = httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, uri),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            await Task.Delay(50, cts.Token);
            Assert.False(response3Task.IsCompleted, "Expected the third request to wait for a local port to become available.");

            response1.Dispose();

            using var response3 = await response3Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            releaseTcs.TrySetResult();
            cts.Cancel();

            try
            {
                await Task.WhenAll(connectionTasks.Concat(new[] { acceptTask, forwarderTask })).WaitAsync(TimeSpan.FromSeconds(2));
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
