using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Sockets;

namespace ZTSharp.Samples.ZeroTierSockets.Commands;

internal static class TcpEchoServerCommand
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
                        if (!SampleParsing.TryParseUShortPort(value, out var parsed))
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

        statePath ??= SampleDefaults.GetDefaultStatePath("zerotier-echo-server");
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
                    ManagedSocket accepted;
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
                        await using var connection = accepted;
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
}
