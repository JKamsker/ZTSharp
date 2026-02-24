using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JKamsker.LibZt.ZeroTier;
using JKamsker.LibZt.ZeroTier.Sockets;

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
        case "server":
            await RunTcpEchoServerAsync(commandArgs).ConfigureAwait(false);
            break;
        case "client":
            await RunTcpEchoClientAsync(commandArgs).ConfigureAwait(false);
            break;
        case "udp-server":
            await RunUdpServerAsync(commandArgs).ConfigureAwait(false);
            break;
        case "udp-client":
            await RunUdpClientAsync(commandArgs).ConfigureAwait(false);
            break;
        case "http-get":
            await RunHttpGetAsync(commandArgs).ConfigureAwait(false);
            break;
        default:
            await Console.Error.WriteLineAsync($"Unknown command '{command}'.").ConfigureAwait(false);
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
          dotnet run -- server --network <nwid> [--state <path>] --port <port>
          dotnet run -- client --network <nwid> [--state <path>] --to <ip:port|url> --message <text>
          dotnet run -- udp-server --network <nwid> [--state <path>] --port <port>
          dotnet run -- udp-client --network <nwid> [--state <path>] --to <ip:port|url> --message <text>
          dotnet run -- http-get --network <nwid> [--state <path>] --url <url>

        Notes:
          - This sample uses the managed-only real ZeroTier stack (no OS adapter required).
          - For socket-like APIs, it uses ZtManagedSocket (compat wrapper over ZtZeroTierSocket).
        """);
}

static async Task RunTcpEchoServerAsync(string[] args)
{
    string? statePath = null;
    string? networkText = null;
    int? port = null;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--state":
                statePath = ReadOptionValue(args, ref i, "--state");
                break;
            case "--network":
                networkText = ReadOptionValue(args, ref i, "--network");
                break;
            case "--port":
            {
                var value = ReadOptionValue(args, ref i, "--port");
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed is < 1 or > ushort.MaxValue)
                {
                    throw new InvalidOperationException("Invalid --port value.");
                }

                port = parsed;
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

    if (port is null)
    {
        throw new InvalidOperationException("Missing --port <port>.");
    }

    statePath ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-samples", "zerotier-echo-server");
    var networkId = ParseNetworkId(networkText);

    using var cts = SetupCancel();
    var token = cts.Token;

    var zt = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
    {
        StateRootPath = statePath,
        NetworkId = networkId
    }, token).ConfigureAwait(false);
    try
    {
        await zt.JoinAsync(token).ConfigureAwait(false);

        Console.WriteLine($"NodeId: {zt.NodeId}");
        foreach (var ip in zt.ManagedIps)
        {
            Console.WriteLine($"Managed IP: {ip}");
        }

        var listener = zt.CreateSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await listener.BindAsync(new IPEndPoint(IPAddress.Any, port.Value), token).ConfigureAwait(false);
            await listener.ListenAsync(backlog: 128, token).ConfigureAwait(false);
            Console.WriteLine($"TCP echo listening on {listener.LocalEndPoint}");

            while (!token.IsCancellationRequested)
            {
                ZtManagedSocket accepted;
                try
                {
                    accepted = await listener.AcceptAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    var connection = accepted;
                    try
                    {
                        var buffer = new byte[64 * 1024];
                        var read = await connection.ReceiveAsync(buffer, token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            return;
                        }

                        var message = Encoding.UTF8.GetString(buffer, 0, read);
                        Console.WriteLine($"Accepted 1 message: '{message}'");
                        await connection.SendAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    finally
                    {
                        try
                        {
                            await connection.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                });
            }
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }
    finally
    {
        await zt.DisposeAsync().ConfigureAwait(false);
    }
}

static async Task RunTcpEchoClientAsync(string[] args)
{
    string? statePath = null;
    string? networkText = null;
    string? toText = null;
    string? message = null;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--state":
                statePath = ReadOptionValue(args, ref i, "--state");
                break;
            case "--network":
                networkText = ReadOptionValue(args, ref i, "--network");
                break;
            case "--to":
                toText = ReadOptionValue(args, ref i, "--to");
                break;
            case "--message":
                message = ReadOptionValue(args, ref i, "--message");
                break;
            default:
                throw new InvalidOperationException($"Unknown option '{arg}'.");
        }
    }

    if (string.IsNullOrWhiteSpace(networkText))
    {
        throw new InvalidOperationException("Missing --network <nwid>.");
    }

    if (string.IsNullOrWhiteSpace(toText))
    {
        throw new InvalidOperationException("Missing --to <ip:port|url>.");
    }

    if (message is null)
    {
        throw new InvalidOperationException("Missing --message <text>.");
    }

    statePath ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-samples", "zerotier-echo-client");
    var networkId = ParseNetworkId(networkText);
    var remote = ParseToEndpoint(toText);

    using var cts = SetupCancel();
    var token = cts.Token;

    var zt = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
    {
        StateRootPath = statePath,
        NetworkId = networkId
    }, token).ConfigureAwait(false);
    try
    {
        var socket = zt.CreateSocket(remote.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(remote, token).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(payload, token).ConfigureAwait(false);

            var received = new byte[payload.Length];
            var total = 0;
            while (total < received.Length)
            {
                var read = await socket.ReceiveAsync(received.AsMemory(total), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            Console.WriteLine($"Echo: '{Encoding.UTF8.GetString(received, 0, total)}'");
        }
        finally
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }
    finally
    {
        await zt.DisposeAsync().ConfigureAwait(false);
    }
}

static async Task RunUdpServerAsync(string[] args)
{
    string? statePath = null;
    string? networkText = null;
    int? port = null;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--state":
                statePath = ReadOptionValue(args, ref i, "--state");
                break;
            case "--network":
                networkText = ReadOptionValue(args, ref i, "--network");
                break;
            case "--port":
            {
                var value = ReadOptionValue(args, ref i, "--port");
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed is < 1 or > ushort.MaxValue)
                {
                    throw new InvalidOperationException("Invalid --port value.");
                }

                port = parsed;
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

    if (port is null)
    {
        throw new InvalidOperationException("Missing --port <port>.");
    }

    statePath ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-samples", "zerotier-udp-server");
    var networkId = ParseNetworkId(networkText);

    using var cts = SetupCancel();
    var token = cts.Token;

    var zt = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
    {
        StateRootPath = statePath,
        NetworkId = networkId
    }, token).ConfigureAwait(false);
    try
    {
        var socket = zt.CreateSocket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            await socket.BindAsync(new IPEndPoint(IPAddress.Any, port.Value), token).ConfigureAwait(false);
            Console.WriteLine($"UDP server bound on {socket.LocalEndPoint}");

            var buffer = new byte[64 * 1024];
            while (!token.IsCancellationRequested)
            {
                (int receivedBytes, EndPoint remoteEndPoint) received;
                try
                {
                    received = await socket.ReceiveFromAsync(buffer, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, received.receivedBytes);
                Console.WriteLine($"UDP recv from {received.remoteEndPoint}: '{message}'");

                var response = message == "ping" ? "pong" : message;
                await socket
                    .SendToAsync(Encoding.UTF8.GetBytes(response), received.remoteEndPoint, token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }
    finally
    {
        await zt.DisposeAsync().ConfigureAwait(false);
    }
}

static async Task RunUdpClientAsync(string[] args)
{
    string? statePath = null;
    string? networkText = null;
    string? toText = null;
    string? message = null;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--state":
                statePath = ReadOptionValue(args, ref i, "--state");
                break;
            case "--network":
                networkText = ReadOptionValue(args, ref i, "--network");
                break;
            case "--to":
                toText = ReadOptionValue(args, ref i, "--to");
                break;
            case "--message":
                message = ReadOptionValue(args, ref i, "--message");
                break;
            default:
                throw new InvalidOperationException($"Unknown option '{arg}'.");
        }
    }

    if (string.IsNullOrWhiteSpace(networkText))
    {
        throw new InvalidOperationException("Missing --network <nwid>.");
    }

    if (string.IsNullOrWhiteSpace(toText))
    {
        throw new InvalidOperationException("Missing --to <ip:port|url>.");
    }

    if (message is null)
    {
        throw new InvalidOperationException("Missing --message <text>.");
    }

    statePath ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-samples", "zerotier-udp-client");
    var networkId = ParseNetworkId(networkText);
    var remote = ParseToEndpoint(toText);

    using var cts = SetupCancel();
    var token = cts.Token;

    var zt = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
    {
        StateRootPath = statePath,
        NetworkId = networkId
    }, token).ConfigureAwait(false);
    try
    {
        var socket = zt.CreateSocket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            await socket.BindAsync(new IPEndPoint(remote.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), token)
                .ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(message);
            await socket.SendToAsync(payload, remote, token).ConfigureAwait(false);

            var buffer = new byte[64 * 1024];
            var received = await socket.ReceiveFromAsync(buffer, token).ConfigureAwait(false);
            var response = Encoding.UTF8.GetString(buffer, 0, received.ReceivedBytes);
            Console.WriteLine($"UDP response: '{response}'");
        }
        finally
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }
    finally
    {
        await zt.DisposeAsync().ConfigureAwait(false);
    }
}

static async Task RunHttpGetAsync(string[] args)
{
    string? statePath = null;
    string? networkText = null;
    string? urlText = null;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--state":
                statePath = ReadOptionValue(args, ref i, "--state");
                break;
            case "--network":
                networkText = ReadOptionValue(args, ref i, "--network");
                break;
            case "--url":
                urlText = ReadOptionValue(args, ref i, "--url");
                break;
            default:
                throw new InvalidOperationException($"Unknown option '{arg}'.");
        }
    }

    if (string.IsNullOrWhiteSpace(networkText))
    {
        throw new InvalidOperationException("Missing --network <nwid>.");
    }

    if (string.IsNullOrWhiteSpace(urlText) || !Uri.TryCreate(urlText, UriKind.Absolute, out var url))
    {
        throw new InvalidOperationException("Missing/invalid --url <url>.");
    }

    statePath ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-samples", "zerotier-http-get");
    var networkId = ParseNetworkId(networkText);

    using var cts = SetupCancel();
    var token = cts.Token;

    var zt = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
    {
        StateRootPath = statePath,
        NetworkId = networkId
    }, token).ConfigureAwait(false);
    try
    {
        await zt.JoinAsync(token).ConfigureAwait(false);

        using var sockets = new SocketsHttpHandler { UseProxy = false };
        sockets.ConnectCallback = async (context, cancellationToken) =>
        {
            if (!IPAddress.TryParse(context.DnsEndPoint.Host, out var ip))
            {
                throw new InvalidOperationException("This sample only supports IP literal hosts (no DNS).");
            }

            return await zt
                .ConnectTcpAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken)
                .ConfigureAwait(false);
        };

        using var http = new HttpClient(sockets, disposeHandler: true);
        var body = await http.GetStringAsync(url, token).ConfigureAwait(false);
        Console.WriteLine(body);
    }
    finally
    {
        await zt.DisposeAsync().ConfigureAwait(false);
    }
}

static CancellationTokenSource SetupCancel()
{
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    return cts;
}

static string ReadOptionValue(string[] args, ref int index, string option)
{
    if (index + 1 >= args.Length)
    {
        throw new InvalidOperationException($"Missing value for {option}.");
    }

    return args[++index];
}

static IPEndPoint ParseToEndpoint(string value)
{
    if (Uri.TryCreate(value, UriKind.Absolute, out var url))
    {
        if (string.IsNullOrWhiteSpace(url.Host) || url.Port <= 0)
        {
            throw new InvalidOperationException("Invalid --to value.");
        }

        if (!IPAddress.TryParse(url.Host, out var hostIp))
        {
            throw new InvalidOperationException("Invalid --to value (host must be an IP literal).");
        }

        return new IPEndPoint(hostIp, url.Port);
    }

    var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
    if (parts.Length != 2 ||
        !IPAddress.TryParse(parts[0], out var ip) ||
        !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
        port is < 1 or > ushort.MaxValue)
    {
        throw new InvalidOperationException("Invalid --to value (expected ip:port or url).");
    }

    return new IPEndPoint(ip, port);
}

static ulong ParseNetworkId(string text)
{
    var span = text.AsSpan().Trim();
    if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        span = span.Slice(2);
    }

    if (span.Length == 0)
    {
        throw new InvalidOperationException("Invalid --network value.");
    }

    return ulong.Parse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
