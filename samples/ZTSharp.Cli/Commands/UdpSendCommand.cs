using System.Net;
using System.Net.Sockets;
using System.Text;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli.Commands;

internal static class UdpSendCommand
{
    public static async Task RunAsync(string[] commandArgs)
    {
        string? statePath = null;
        string? networkText = null;
        var stack = "managed";
        IPEndPoint? destination = null;
        string? dataText = null;

        for (var i = 0; i < commandArgs.Length; i++)
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
                case "--to":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--to");
                        destination = CliParsing.ParseIpEndpoint(value);
                        break;
                    }
                case "--data":
                    dataText = CliParsing.ReadOptionValue(commandArgs, ref i, "--data");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown option '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(networkText))
        {
            throw new InvalidOperationException("Missing --network <nwid>.");
        }

        if (destination is null)
        {
            throw new InvalidOperationException("Missing --to <ip:port>.");
        }

        if (string.IsNullOrWhiteSpace(dataText))
        {
            throw new InvalidOperationException("Missing --data <text>.");
        }

        statePath ??= CliDefaults.CreateTemporaryStatePath();
        var networkId = CliParsing.ParseNetworkId(networkText);
        stack = CliParsing.NormalizeStack(stack);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (string.Equals(stack, "managed", StringComparison.OrdinalIgnoreCase))
        {
            await RunUdpSendZeroTierAsync(statePath, networkId, destination, dataText, cts.Token).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("Invalid --stack value (expected managed).");
    }

    private static async Task RunUdpSendZeroTierAsync(
        string statePath,
        ulong networkId,
        IPEndPoint destination,
        string dataText,
        CancellationToken cancellationToken)
    {
        var socket = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
        {
            StateRootPath = statePath,
            NetworkId = networkId
        }, cancellationToken).ConfigureAwait(false);

        ZeroTierUdpSocket? udp = null;

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

            if (destination.AddressFamily == AddressFamily.InterNetwork)
            {
                udp = await socket.BindUdpAsync(0, cancellationToken).ConfigureAwait(false);
            }
            else if (destination.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var localV6 = socket.ManagedIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6);
                if (localV6 is null)
                {
                    throw new InvalidOperationException("No IPv6 managed IP assigned for this network.");
                }

                udp = await socket.BindUdpAsync(localV6, 0, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new NotSupportedException($"Unsupported address family: {destination.AddressFamily}.");
            }

            var bytes = Encoding.UTF8.GetBytes(dataText);
            var sent = await udp.SendToAsync(bytes, destination, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"UDP Send: {udp.LocalEndpoint} -> {destination} ({sent} bytes)");
        }
        finally
        {
            if (udp is not null)
            {
                try
                {
                    await udp.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }
}
