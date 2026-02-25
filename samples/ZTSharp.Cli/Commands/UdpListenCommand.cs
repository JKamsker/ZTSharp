using System.Net.Sockets;
using System.Text;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli.Commands;

internal static class UdpListenCommand
{
    public static async Task RunAsync(string[] commandArgs)
    {
        if (commandArgs.Length == 0 || commandArgs[0].StartsWith('-'))
        {
            throw new InvalidOperationException("Missing <localPort>.");
        }

        var localPort = CliParsing.ParseUShortPort(commandArgs[0], "<localPort>");

        string? statePath = null;
        string? networkText = null;
        var stack = "managed";

        for (var i = 1; i < commandArgs.Length; i++)
        {
            var arg = commandArgs[i];
            switch (arg)
            {
                case "--state":
                    statePath = CliParsing.ReadOptionValue(commandArgs, ref i, "--state");
                    break;
                case "--network":
                    networkText = CliParsing.ReadOptionValue(commandArgs, ref i, "--network");
                    break;
                case "--stack":
                    stack = CliParsing.ReadOptionValue(commandArgs, ref i, "--stack");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown option '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(networkText))
        {
            throw new InvalidOperationException("Missing --network <nwid>.");
        }

        statePath ??= CliDefaults.CreateTemporaryStatePath();
        var networkId = CliParsing.ParseNetworkId(networkText);
        stack = CliParsing.NormalizeStack(stack);

        using var cancellation = ConsoleCancellation.Create();

        if (string.Equals(stack, "managed", StringComparison.OrdinalIgnoreCase))
        {
            await RunUdpListenZeroTierAsync(statePath, networkId, localPort, cancellation.Token).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("Invalid --stack value (expected managed).");
    }

    private static async Task RunUdpListenZeroTierAsync(
        string statePath,
        ulong networkId,
        int listenPort,
        CancellationToken cancellationToken)
    {
        if (listenPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(listenPort));
        }

        var socket = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
        {
            StateRootPath = statePath,
            NetworkId = networkId
        }, cancellationToken).ConfigureAwait(false);

        ZeroTierUdpSocket? udp4 = null;
        ZeroTierUdpSocket? udp6 = null;

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

            var managedIp4 = socket.ManagedIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            var managedIp6 = socket.ManagedIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6);

            if (managedIp4 is null && managedIp6 is null)
            {
                throw new InvalidOperationException("No managed IPs assigned for this network.");
            }

            if (managedIp4 is not null)
            {
                udp4 = await socket.BindUdpAsync(managedIp4, listenPort, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"UDP Listen: {managedIp4}:{listenPort}");
            }

            if (managedIp6 is not null)
            {
                udp6 = await socket.BindUdpAsync(managedIp6, listenPort, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"UDP Listen: [{managedIp6}]:{listenPort}");
            }

            var pongBytes = Encoding.UTF8.GetBytes("pong");

            var listeners = new List<Task>(capacity: 2);
            if (udp4 is not null)
            {
                listeners.Add(RunUdpEchoLoopAsync(udp4, pongBytes, cancellationToken));
            }

            if (udp6 is not null)
            {
                listeners.Add(RunUdpEchoLoopAsync(udp6, pongBytes, cancellationToken));
            }

            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
        finally
        {
            await DisposeUdpQuietlyAsync(udp4).ConfigureAwait(false);
            await DisposeUdpQuietlyAsync(udp6).ConfigureAwait(false);

            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeUdpQuietlyAsync(ZeroTierUdpSocket? udp)
    {
        if (udp is null)
        {
            return;
        }

        try
        {
            await udp.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task RunUdpEchoLoopAsync(ZeroTierUdpSocket udp, byte[] pongBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[ushort.MaxValue];

        while (!cancellationToken.IsCancellationRequested)
        {
            ZeroTierUdpReceiveResult received;
            try
            {
                received = await udp.ReceiveFromAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, received.ReceivedBytes);
            Console.WriteLine($"UDP RX {udp.LocalEndpoint} <- {received.RemoteEndPoint}: {text}");

            if (received.ReceivedBytes == 4 &&
                buffer[0] == (byte)'p' &&
                buffer[1] == (byte)'i' &&
                buffer[2] == (byte)'n' &&
                buffer[3] == (byte)'g')
            {
                try
                {
                    await udp.SendToAsync(pongBytes, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (SocketException)
                {
                }
            }
        }
    }
}
