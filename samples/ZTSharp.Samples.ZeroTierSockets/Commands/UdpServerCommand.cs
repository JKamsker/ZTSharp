using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ZTSharp.ZeroTier;

namespace ZTSharp.Samples.ZeroTierSockets.Commands;

internal static class UdpServerCommand
{
    public static async Task RunAsync(string[] args)
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
                    statePath = SampleParsing.ReadOptionValue(args, ref i, "--state");
                    break;
                case "--network":
                    networkText = SampleParsing.ReadOptionValue(args, ref i, "--network");
                    break;
                case "--port":
                    {
                        var value = SampleParsing.ReadOptionValue(args, ref i, "--port");
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
        var networkId = SampleParsing.ParseNetworkId(networkText);

        using var cts = ConsoleCancellation.Setup();
        var token = cts.Token;

        var zt = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
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
}

