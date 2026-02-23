using System.Globalization;
using System.Net;
using JKamsker.LibZt.Http;
using JKamsker.LibZt;
using JKamsker.LibZt.Sockets;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return;
}

try
{
    var command = args[0];
    var commandArgs = args.Skip(1).ToArray();
    switch (command)
    {
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
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    Environment.ExitCode = 1;
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        Usage:
          libzt expose <localPort> --network <nwid> [options]
          libzt call --network <nwid> --url <url> [options]

        Options:
          --listen <port>             Overlay listen port (default: <localPort>)
          --to <host:port>            Forward target (default: 127.0.0.1:<localPort>)
          --state <path>              State directory (default: temp folder)
          --transport <osudp|inmem>   Transport mode (default: osudp)
          --udp-port <port>           OS UDP listen port (osudp only, default: 0)
          --advertise <ip[:port]>     Advertised UDP endpoint for peers (osudp only)
          --peer <nodeId@ip:port>     Add an OS UDP peer (repeatable)
        """);
}

static async Task RunCallAsync(string[] commandArgs)
{
    string? statePath = null;
    string? networkText = null;
    string? urlText = null;
    var transportMode = ZtTransportMode.OsUdp;
    var udpListenPort = 0;
    IPEndPoint? advertisedEndpoint = null;
    var peers = new List<(ulong NodeId, IPEndPoint Endpoint)>();

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
            case "--url":
                urlText = ReadOptionValue(commandArgs, ref i, "--url");
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

    if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url))
    {
        throw new InvalidOperationException("Invalid --url value.");
    }

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

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

        using var handler = new ZtOverlayHttpMessageHandler(node, networkId);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
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
