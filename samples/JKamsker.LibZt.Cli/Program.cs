using System.Globalization;
using System.Net.Sockets;
using System.Net;
using System.Threading.Channels;
using JKamsker.LibZt.Http;
using JKamsker.LibZt;
using JKamsker.LibZt.Sockets;
using JKamsker.LibZt.ZeroTier;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return;
}

var trace = bool.TryParse(Environment.GetEnvironmentVariable("LIBZT_CLI_TRACE"), out var parsedTrace) && parsedTrace;

try
{
    var command = args[0];
    var commandArgs = args.Skip(1).ToArray();
    switch (command)
    {
        case "join":
            await RunJoinAsync(commandArgs).ConfigureAwait(false);
            break;
        case "expose":
            await RunExposeAsync(commandArgs).ConfigureAwait(false);
            break;
        case "call":
            await RunCallAsync(commandArgs).ConfigureAwait(false);
            break;
        default:
            await Console.Error
                .WriteLineAsync($"Unknown command '{command}'.")
                .ConfigureAwait(false);
            PrintHelp();
            Environment.ExitCode = 2;
            break;
    }
}
#pragma warning disable CA1031
catch (Exception ex)
#pragma warning restore CA1031
{
    await Console.Error.WriteLineAsync(trace ? ex.ToString() : ex.Message).ConfigureAwait(false);
    Environment.ExitCode = 1;
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        Usage:
          libzt join --network <nwid> [options]
          libzt expose <localPort> --network <nwid> [options]
          libzt call --network <nwid> --url <url> [options]

        Options:
          --listen <port>             Listen port (default: <localPort>)
          --to <host:port>            Forward target (default: 127.0.0.1:<localPort>)
          --state <path>              State directory (default: temp folder)
          --stack <managed|overlay>   Node stack (default: managed; 'zerotier' and 'libzt' are aliases for 'managed')
          --transport <osudp|inmem>   Transport mode (default: osudp)
          --udp-port <port>           OS UDP listen port (osudp only, default: 0)
          --advertise <ip[:port]>     Advertised UDP endpoint for peers (osudp only)
          --peer <nodeId@ip:port>     Add an OS UDP peer (repeatable)
          --http <overlay|os>         HTTP mode for 'call' (default: overlay)
          --map-ip <ip=nodeId>        Map IP to node id for overlay HTTP (repeatable)
          --once                      For 'join': initialize and exit
        """);
}

static async Task RunJoinAsync(string[] commandArgs)
{
    string? statePath = null;
    string? networkText = null;
    var stack = "managed";
    var transportMode = ZtTransportMode.OsUdp;
    var udpListenPort = 0;
    IPEndPoint? advertisedEndpoint = null;
    var peers = new List<(ulong NodeId, IPEndPoint Endpoint)>();
    var once = false;

    for (var i = 0; i < commandArgs.Length; i++)
    {
        var arg = commandArgs[i];
        switch (arg)
        {
            case "--state":
                statePath = ReadOptionValue(commandArgs, ref i, "--state");
                break;
            case "--network":
                networkText = ReadOptionValue(commandArgs, ref i, "--network");
                break;
            case "--stack":
                stack = ReadOptionValue(commandArgs, ref i, "--stack");
                break;
            case "--transport":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--transport");
                transportMode = value switch
                {
                    "osudp" => ZtTransportMode.OsUdp,
                    "inmem" => ZtTransportMode.InMemory,
                    _ => throw new InvalidOperationException("Invalid --transport value (expected osudp|inmem).")
                };
                break;
            }
            case "--udp-port":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--udp-port");
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed is < 0 or > ushort.MaxValue)
                {
                    throw new InvalidOperationException("Invalid --udp-port value.");
                }

                udpListenPort = parsed;
                break;
            }
            case "--advertise":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--advertise");
                advertisedEndpoint = ParseIpEndpoint(value);
                break;
            }
            case "--peer":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--peer");
                var parsed = ParsePeer(value);
                peers.Add(parsed);
                break;
            }
            case "--once":
                once = true;
                break;
            default:
                throw new InvalidOperationException($"Unknown option '{arg}'.");
        }
    }

    if (string.IsNullOrWhiteSpace(networkText))
    {
        throw new InvalidOperationException("Missing --network <nwid>.");
    }

    statePath ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-cli", "node-" + Guid.NewGuid().ToString("N"));
    var networkId = ParseNetworkId(networkText);
    stack = NormalizeStack(stack);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    if (string.Equals(stack, "managed", StringComparison.OrdinalIgnoreCase))
    {
        await RunJoinZeroTierAsync(statePath, networkId, once, cts.Token).ConfigureAwait(false);
        return;
    }

    if (!string.Equals(stack, "overlay", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Invalid --stack value (expected managed|overlay).");
    }

    var node = new ZtNode(new ZtNodeOptions
    {
        StateRootPath = statePath,
        TransportMode = transportMode,
        UdpListenPort = transportMode == ZtTransportMode.OsUdp ? udpListenPort : null,
        EnablePeerDiscovery = true,
        AdvertisedTransportEndpoint = advertisedEndpoint
    });

    try
    {
        await node.StartAsync(cts.Token).ConfigureAwait(false);
        await node.JoinNetworkAsync(networkId, cts.Token).ConfigureAwait(false);

        if (transportMode == ZtTransportMode.OsUdp && peers.Count != 0)
        {
            foreach (var peer in peers)
            {
                await node.AddPeerAsync(networkId, peer.NodeId, peer.Endpoint, cts.Token).ConfigureAwait(false);
            }
        }

        var localUdp = node.LocalTransportEndpoint;
        Console.WriteLine($"State: {statePath}");
        Console.WriteLine($"NodeId: {node.NodeId}");
        if (localUdp is not null)
        {
            Console.WriteLine($"Local UDP: {localUdp}");
        }

        if (once)
        {
            return;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token).ConfigureAwait(false);
    }
    finally
    {
        await node.DisposeAsync().ConfigureAwait(false);
    }
}

static async Task RunJoinZeroTierAsync(string statePath, ulong networkId, bool once, CancellationToken cancellationToken)
{
    var socket = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
    {
        StateRootPath = statePath,
        NetworkId = networkId
    }, cancellationToken).ConfigureAwait(false);

    try
    {
        Console.WriteLine($"State: {statePath}");
        Console.WriteLine($"NodeId: {socket.NodeId}");

        await socket.JoinAsync(cancellationToken).ConfigureAwait(false);

        if (socket.ManagedIps.Count != 0)
        {
            Console.WriteLine("Managed IPs:");
            foreach (var ip in socket.ManagedIps)
            {
                Console.WriteLine($"  {ip}");
            }
        }

        if (once)
        {
            return;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
        await socket.DisposeAsync().ConfigureAwait(false);
    }
}

static async Task RunCallAsync(string[] commandArgs)
{
    string? statePath = null;
    string? networkText = null;
    string? urlText = null;
    var stack = "managed";
    var transportMode = ZtTransportMode.OsUdp;
    var udpListenPort = 0;
    IPEndPoint? advertisedEndpoint = null;
    var peers = new List<(ulong NodeId, IPEndPoint Endpoint)>();
    var httpMode = "overlay";
    var ipMappings = new List<(IPAddress Address, ulong NodeId)>();

    for (var i = 0; i < commandArgs.Length; i++)
    {
        var arg = commandArgs[i];
        switch (arg)
        {
            case "--state":
                statePath = ReadOptionValue(commandArgs, ref i, "--state");
                break;
            case "--network":
                networkText = ReadOptionValue(commandArgs, ref i, "--network");
                break;
            case "--stack":
                stack = ReadOptionValue(commandArgs, ref i, "--stack");
                break;
            case "--url":
                urlText = ReadOptionValue(commandArgs, ref i, "--url");
                break;
            case "--http":
                httpMode = ReadOptionValue(commandArgs, ref i, "--http");
                break;
            case "--map-ip":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--map-ip");
                var mapping = ParseIpMapping(value);
                ipMappings.Add(mapping);
                break;
            }
            case "--transport":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--transport");
                transportMode = value switch
                {
                    "osudp" => ZtTransportMode.OsUdp,
                    "inmem" => ZtTransportMode.InMemory,
                    _ => throw new InvalidOperationException("Invalid --transport value (expected osudp|inmem).")
                };
                break;
            }
            case "--udp-port":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--udp-port");
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed is < 0 or > ushort.MaxValue)
                {
                    throw new InvalidOperationException("Invalid --udp-port value.");
                }

                udpListenPort = parsed;
                break;
            }
            case "--advertise":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--advertise");
                advertisedEndpoint = ParseIpEndpoint(value);
                break;
            }
            case "--peer":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--peer");
                var parsed = ParsePeer(value);
                peers.Add(parsed);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown option '{arg}'.");
        }
    }

    if (string.IsNullOrWhiteSpace(networkText))
    {
        throw new InvalidOperationException("Missing --network <nwid>.");
    }

    if (string.IsNullOrWhiteSpace(urlText))
    {
        throw new InvalidOperationException("Missing --url <url>.");
    }

    statePath ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-cli", "node-" + Guid.NewGuid().ToString("N"));
    var networkId = ParseNetworkId(networkText);
    stack = NormalizeStack(stack);

    if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url))
    {
        throw new InvalidOperationException("Invalid --url value.");
    }

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    if (string.Equals(stack, "managed", StringComparison.OrdinalIgnoreCase))
    {
        await RunCallZeroTierAsync(statePath, networkId, url, cts.Token).ConfigureAwait(false);
        return;
    }

    if (!string.Equals(stack, "overlay", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Invalid --stack value (expected managed|overlay).");
    }

    var node = new ZtNode(new ZtNodeOptions
    {
        StateRootPath = statePath,
        TransportMode = transportMode,
        UdpListenPort = transportMode == ZtTransportMode.OsUdp ? udpListenPort : null,
        EnablePeerDiscovery = true,
        AdvertisedTransportEndpoint = advertisedEndpoint
    });

    try
    {
        await node.StartAsync(cts.Token).ConfigureAwait(false);
        await node.JoinNetworkAsync(networkId, cts.Token).ConfigureAwait(false);

        if (transportMode == ZtTransportMode.OsUdp && peers.Count != 0)
        {
            foreach (var peer in peers)
            {
                await node.AddPeerAsync(networkId, peer.NodeId, peer.Endpoint, cts.Token).ConfigureAwait(false);
            }
        }

        var localUdp = node.LocalTransportEndpoint;
        Console.WriteLine($"NodeId: {node.NodeId}");
        if (localUdp is not null)
        {
            Console.WriteLine($"Local UDP: {localUdp}");
        }

        using var httpClient = CreateHttpClient(node, networkId, httpMode, ipMappings);
        var response = await httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusCode}");
        Console.WriteLine(body);
    }
    finally
    {
        await node.DisposeAsync().ConfigureAwait(false);
    }
}

static async Task RunCallZeroTierAsync(string statePath, ulong networkId, Uri url, CancellationToken cancellationToken)
{
    var socket = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
    {
        StateRootPath = statePath,
        NetworkId = networkId
    }, cancellationToken).ConfigureAwait(false);

    try
    {
        Console.WriteLine($"NodeId: {socket.NodeId}");

        await socket.JoinAsync(cancellationToken).ConfigureAwait(false);
        if (socket.ManagedIps.Count != 0)
        {
            Console.WriteLine("Managed IPs:");
            foreach (var ip in socket.ManagedIps)
            {
                Console.WriteLine($"  {ip}");
            }
        }

        using var httpClient = socket.CreateHttpClient();
        var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusCode}");
        Console.WriteLine(body);
    }
    finally
    {
        await socket.DisposeAsync().ConfigureAwait(false);
    }
}

[global::System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Handler ownership transfers to HttpClient, which is disposed by the caller.")]
static HttpClient CreateHttpClient(
    ZtNode node,
    ulong networkId,
    string httpMode,
    IReadOnlyList<(IPAddress Address, ulong NodeId)> ipMappings)
{
    if (string.Equals(httpMode, "os", StringComparison.OrdinalIgnoreCase))
    {
        return new HttpClient(new SocketsHttpHandler { UseProxy = false });
    }

    if (!string.Equals(httpMode, "overlay", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Invalid --http value (expected overlay|os).");
    }

    ZtOverlayAddressBook? book = null;
    if (ipMappings.Count != 0)
    {
        book = new ZtOverlayAddressBook();
        foreach (var mapping in ipMappings)
        {
            book.Add(mapping.Address, mapping.NodeId);
        }
    }

    var handler = new ZtOverlayHttpMessageHandler(
        node,
        networkId,
        book is null ? null : new ZtOverlayHttpMessageHandlerOptions { AddressBook = book });

    return new HttpClient(handler, disposeHandler: true);
}

static async Task RunExposeAsync(string[] commandArgs)
{
    if (commandArgs.Length == 0 || commandArgs[0].StartsWith('-'))
    {
        throw new InvalidOperationException("Missing <localPort>.");
    }

    if (!int.TryParse(commandArgs[0], NumberStyles.None, CultureInfo.InvariantCulture, out var localPort) ||
        localPort is < 1 or > ushort.MaxValue)
    {
        throw new InvalidOperationException("Invalid <localPort>.");
    }

    string? statePath = null;
    string? networkText = null;
    var stack = "managed";
    int? overlayListenPort = null;
    (string Host, int Port)? target = null;
    var transportMode = ZtTransportMode.OsUdp;
    var udpListenPort = 0;
    IPEndPoint? advertisedEndpoint = null;
    var peers = new List<(ulong NodeId, IPEndPoint Endpoint)>();

    for (var i = 1; i < commandArgs.Length; i++)
    {
        var arg = commandArgs[i];
        switch (arg)
        {
            case "--state":
                statePath = ReadOptionValue(commandArgs, ref i, "--state");
                break;
            case "--network":
                networkText = ReadOptionValue(commandArgs, ref i, "--network");
                break;
            case "--stack":
                stack = ReadOptionValue(commandArgs, ref i, "--stack");
                break;
            case "--listen":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--listen");
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed is < 1 or > ushort.MaxValue)
                {
                    throw new InvalidOperationException("Invalid --listen value.");
                }

                overlayListenPort = parsed;
                break;
            }
            case "--to":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--to");
                target = ParseHostPort(value);
                break;
            }
            case "--transport":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--transport");
                transportMode = value switch
                {
                    "osudp" => ZtTransportMode.OsUdp,
                    "inmem" => ZtTransportMode.InMemory,
                    _ => throw new InvalidOperationException("Invalid --transport value (expected osudp|inmem).")
                };
                break;
            }
            case "--udp-port":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--udp-port");
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed is < 0 or > ushort.MaxValue)
                {
                    throw new InvalidOperationException("Invalid --udp-port value.");
                }

                udpListenPort = parsed;
                break;
            }
            case "--advertise":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--advertise");
                advertisedEndpoint = ParseIpEndpoint(value);
                break;
            }
            case "--peer":
            {
                var value = ReadOptionValue(commandArgs, ref i, "--peer");
                var parsed = ParsePeer(value);
                peers.Add(parsed);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown option '{arg}'.");
        }
    }

    if (string.IsNullOrWhiteSpace(networkText))
    {
        throw new InvalidOperationException("Missing --network <nwid>.");
    }

    var networkId = ParseNetworkId(networkText);
    overlayListenPort ??= localPort;
    target ??= ("127.0.0.1", localPort);

    statePath ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-cli", "node-" + Guid.NewGuid().ToString("N"));

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    stack = NormalizeStack(stack);

    if (string.Equals(stack, "managed", StringComparison.OrdinalIgnoreCase))
    {
        await RunExposeZeroTierAsync(statePath, networkId, overlayListenPort.Value, target.Value.Host, target.Value.Port, cts.Token).ConfigureAwait(false);
        return;
    }

    if (!string.Equals(stack, "overlay", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Invalid --stack value (expected managed|overlay).");
    }

    var node = new ZtNode(new ZtNodeOptions
    {
        StateRootPath = statePath,
        TransportMode = transportMode,
        UdpListenPort = transportMode == ZtTransportMode.OsUdp ? udpListenPort : null,
        EnablePeerDiscovery = true,
        AdvertisedTransportEndpoint = advertisedEndpoint
    });

    try
    {
        await node.StartAsync(cts.Token).ConfigureAwait(false);
        await node.JoinNetworkAsync(networkId, cts.Token).ConfigureAwait(false);

        if (transportMode == ZtTransportMode.OsUdp && peers.Count != 0)
        {
            foreach (var peer in peers)
            {
                await node.AddPeerAsync(networkId, peer.NodeId, peer.Endpoint, cts.Token).ConfigureAwait(false);
            }
        }

        var localUdp = node.LocalTransportEndpoint;
        Console.WriteLine($"NodeId: {node.NodeId}");
        if (localUdp is not null)
        {
            Console.WriteLine($"Local UDP: {localUdp}");
        }
        if (advertisedEndpoint is not null)
        {
            Console.WriteLine($"Advertise UDP: {advertisedEndpoint}");
        }

        Console.WriteLine($"Expose: http://{node.NodeId}:{overlayListenPort}/ -> {target.Value.Host}:{target.Value.Port}");

        var forwarder = new ZtOverlayTcpPortForwarder(
            node,
            networkId,
            overlayListenPort.Value,
            target.Value.Host,
            target.Value.Port);

        try
        {
            await forwarder.RunAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            await forwarder.DisposeAsync().ConfigureAwait(false);
        }
    }
    finally
    {
        await node.DisposeAsync().ConfigureAwait(false);
    }
}

static async Task RunExposeZeroTierAsync(
    string statePath,
    ulong networkId,
    int listenPort,
    string targetHost,
    int targetPort,
    CancellationToken cancellationToken)
{
    if (listenPort is < 1 or > ushort.MaxValue)
    {
        throw new ArgumentOutOfRangeException(nameof(listenPort));
    }

    var socket = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
    {
        StateRootPath = statePath,
        NetworkId = networkId
    }, cancellationToken).ConfigureAwait(false);

    ZtZeroTierTcpListener? listener = null;
    using var exposeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var exposeToken = exposeCts.Token;
    Task[]? acceptors = null;

    try
    {
        Console.WriteLine($"NodeId: {socket.NodeId}");

        await socket.JoinAsync(exposeToken).ConfigureAwait(false);
        if (socket.ManagedIps.Count != 0)
        {
            Console.WriteLine("Managed IPs:");
            foreach (var ip in socket.ManagedIps)
            {
                Console.WriteLine($"  {ip}");
            }
        }

        var managedIp = socket.ManagedIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
        if (managedIp is null)
        {
            throw new InvalidOperationException("No IPv4 managed IP assigned for this network.");
        }

        listener = await socket.ListenTcpAsync(listenPort, exposeToken).ConfigureAwait(false);

        Console.WriteLine($"Expose: http://{managedIp}:{listenPort}/ -> {targetHost}:{targetPort}");

        var acceptorCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
        acceptors = new Task[acceptorCount];

        for (var i = 0; i < acceptors.Length; i++)
        {
            acceptors[i] = RunExposeAcceptorAsync(listener, targetHost, targetPort, exposeToken);
        }

        await Task.WhenAll(acceptors).ConfigureAwait(false);
    }
    finally
    {
        try
        {
            await exposeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        if (acceptors is not null)
        {
            try
            {
                await Task.WhenAll(acceptors).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (exposeToken.IsCancellationRequested)
            {
            }
        }

        if (listener is not null)
        {
            try
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await socket.DisposeAsync().ConfigureAwait(false);
    }
}

static async Task RunExposeAcceptorAsync(
    ZtZeroTierTcpListener listener,
    string targetHost,
    int targetPort,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        Stream accepted;
        try
        {
            accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (ChannelClosedException)
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }

        try
        {
            await HandleExposeConnectionAsync(accepted, targetHost, targetPort, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

static async Task HandleExposeConnectionAsync(Stream accepted, string targetHost, int targetPort, CancellationToken cancellationToken)
{
    var overlayStream = accepted;
    var localClient = new TcpClient { NoDelay = true };

    try
    {
        await localClient.ConnectAsync(targetHost, targetPort, cancellationToken).ConfigureAwait(false);
        var localStream = localClient.GetStream();
        try
        {
            using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = bridgeCts.Token;

            var overlayToLocal = CopyAsync(overlayStream, localStream, token);
            var localToOverlay = CopyAsync(localStream, overlayStream, token);

            try
            {
                _ = await Task.WhenAny(overlayToLocal, localToOverlay).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await bridgeCts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }

                try
                {
                    await Task.WhenAll(overlayToLocal, localToOverlay).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (SocketException)
                {
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        finally
        {
            try
            {
                await localStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (SocketException)
    {
    }
    finally
    {
        localClient.Dispose();

        try
        {
            await overlayStream.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
{
    try
    {
        await source.CopyToAsync(destination, bufferSize: 64 * 1024, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (IOException)
    {
    }
    catch (ObjectDisposedException)
    {
    }
}

static string NormalizeStack(string stack)
{
    if (string.Equals(stack, "zerotier", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stack, "libzt", StringComparison.OrdinalIgnoreCase))
    {
        return "managed";
    }

    return stack;
}

static string ReadOptionValue(string[] args, ref int index, string name)
{
    if (index + 1 >= args.Length)
    {
        throw new InvalidOperationException($"Missing value for {name}.");
    }

    index++;
    return args[index];
}

static ulong ParseNetworkId(string text)
{
    var span = text.AsSpan().Trim();
    var hasHexPrefix = false;
    if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        hasHexPrefix = true;
        span = span.Slice(2);
    }

    if (span.Length == 0)
    {
        throw new InvalidOperationException("Invalid --network value.");
    }

    var treatAsHex = hasHexPrefix || span.Length == 16 || ContainsHexLetters(span);
    if (treatAsHex)
    {
        if (!IsHex(span))
        {
            throw new InvalidOperationException("Invalid --network value.");
        }

        return ulong.Parse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    return ulong.Parse(span, NumberStyles.None, CultureInfo.InvariantCulture);
}

static bool ContainsHexLetters(ReadOnlySpan<char> value)
{
    for (var i = 0; i < value.Length; i++)
    {
        var c = value[i];
        if (c is >= 'a' and <= 'f' or >= 'A' and <= 'F')
        {
            return true;
        }
    }

    return false;
}

static bool IsHex(ReadOnlySpan<char> value)
{
    for (var i = 0; i < value.Length; i++)
    {
        var c = value[i];
        if (c is >= '0' and <= '9')
        {
            continue;
        }

        if (c is >= 'a' and <= 'f')
        {
            continue;
        }

        if (c is >= 'A' and <= 'F')
        {
            continue;
        }

        return false;
    }

    return true;
}

static (string Host, int Port) ParseHostPort(string value)
{
    try
    {
        var uri = new Uri("http://" + value);
        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("Invalid endpoint.");
        }

        return (uri.Host, uri.Port);
    }
    catch (UriFormatException)
    {
        throw new InvalidOperationException("Invalid endpoint format. Expected host:port.");
    }
}

static IPEndPoint ParseIpEndpoint(string value)
{
    var (host, port) = ParseHostPort(value);
    if (!IPAddress.TryParse(host, out var ip))
    {
        throw new InvalidOperationException("Invalid --advertise value (expected IP[:port]).");
    }

    return new IPEndPoint(ip, port);
}

static (ulong NodeId, IPEndPoint Endpoint) ParsePeer(string value)
{
    var at = value.IndexOf('@', StringComparison.Ordinal);
    if (at <= 0 || at == value.Length - 1)
    {
        throw new InvalidOperationException("Invalid --peer value (expected nodeId@ip:port).");
    }

    var nodeIdText = value.Substring(0, at);
    var endpointText = value.Substring(at + 1);

    var nodeId = ParseNodeId(nodeIdText);
    var endpoint = ParseIpEndpoint(endpointText);
    return (nodeId, endpoint);
}

static ulong ParseNodeId(string text)
{
    var span = text.AsSpan().Trim();
    var hasHexPrefix = false;
    if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        hasHexPrefix = true;
        span = span.Slice(2);
    }

    if (span.Length == 0)
    {
        throw new InvalidOperationException("Invalid nodeId.");
    }

    var treatAsHex = hasHexPrefix || span.Length == 10 || ContainsHexLetters(span);
    if (treatAsHex)
    {
        if (!IsHex(span))
        {
            throw new InvalidOperationException("Invalid nodeId.");
        }

        var parsed = ulong.Parse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (parsed == 0 || parsed > ZtNodeId.MaxValue)
        {
            throw new InvalidOperationException("Invalid nodeId.");
        }

        return parsed;
    }

    var parsedDec = ulong.Parse(span, NumberStyles.None, CultureInfo.InvariantCulture);
    if (parsedDec == 0 || parsedDec > ZtNodeId.MaxValue)
    {
        throw new InvalidOperationException("Invalid nodeId.");
    }

    return parsedDec;
}

static (IPAddress Address, ulong NodeId) ParseIpMapping(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException("Invalid --map-ip value.");
    }

    var equals = value.IndexOf('=', StringComparison.Ordinal);
    if (equals <= 0 || equals == value.Length - 1)
    {
        throw new InvalidOperationException("Invalid --map-ip value (expected ip=nodeId).");
    }

    var ipText = value.Substring(0, equals);
    var nodeIdText = value.Substring(equals + 1);

    if (!IPAddress.TryParse(ipText, out var ip))
    {
        throw new InvalidOperationException("Invalid --map-ip value (expected ip=nodeId).");
    }

    var nodeId = ParseNodeId(nodeIdText);
    return (ip, nodeId);
}
