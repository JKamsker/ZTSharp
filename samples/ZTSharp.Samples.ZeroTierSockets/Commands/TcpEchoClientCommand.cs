using System.Net.Sockets;
using System.Text;
using ZTSharp.ZeroTier;

namespace ZTSharp.Samples.ZeroTierSockets.Commands;

internal static class TcpEchoClientCommand
{
    public static async Task RunAsync(string[] args)
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
                    statePath = SampleParsing.ReadOptionValue(args, ref i, "--state");
                    break;
                case "--network":
                    networkText = SampleParsing.ReadOptionValue(args, ref i, "--network");
                    break;
                case "--to":
                    toText = SampleParsing.ReadOptionValue(args, ref i, "--to");
                    break;
                case "--message":
                    message = SampleParsing.ReadOptionValue(args, ref i, "--message");
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
        var networkId = SampleParsing.ParseNetworkId(networkText);
        var remote = SampleParsing.ParseToEndpoint(toText);

        using var cts = ConsoleCancellation.Setup();
        var token = cts.Token;

        var zt = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
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
}

