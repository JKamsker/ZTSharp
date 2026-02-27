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

        var mpEnabled = false;
        var mpBondPolicy = ZeroTierBondPolicy.Off;
        int? mpUdpSockets = null;
        IReadOnlyList<int>? mpUdpPorts = null;
        var mpWarmupRoot = true;

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
                case "--multipath":
                    mpEnabled = true;
                    break;
                case "--mp-bond":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--mp-bond");
                        mpBondPolicy = CliParsing.ParseBondPolicy(value);
                        mpEnabled = true;
                        break;
                    }
                case "--mp-udp-sockets":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--mp-udp-sockets");
                        mpUdpSockets = CliParsing.ParsePositiveInt(value, "--mp-udp-sockets value");
                        mpEnabled = true;
                        break;
                    }
                case "--mp-udp-ports":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--mp-udp-ports");
                        mpUdpPorts = CliParsing.ParsePortList(value, "--mp-udp-ports value");
                        mpEnabled = true;
                        break;
                    }
                case "--mp-warmup-root":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--mp-warmup-root");
                        if (!bool.TryParse(value, out mpWarmupRoot))
                        {
                            throw new InvalidOperationException("Invalid --mp-warmup-root value (expected true|false).");
                        }

                        mpEnabled = true;
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

        using var cancellation = ConsoleCancellation.Create();

        if (string.Equals(stack, "managed", StringComparison.OrdinalIgnoreCase))
        {
            var multipath = new ZeroTierMultipathOptions
            {
                Enabled = mpEnabled,
                BondPolicy = mpBondPolicy,
                UdpSocketCount = mpUdpSockets ?? 1,
                LocalUdpPorts = mpUdpPorts,
                WarmupDuplicateToRoot = mpWarmupRoot
            };

            await RunUdpSendZeroTierAsync(statePath, networkId, multipath, destination, dataText, cancellation.Token).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("Invalid --stack value (expected managed).");
    }

    private static async Task RunUdpSendZeroTierAsync(
        string statePath,
        ulong networkId,
        ZeroTierMultipathOptions multipath,
        IPEndPoint destination,
        string dataText,
        CancellationToken cancellationToken)
    {
        var socket = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
        {
            StateRootPath = statePath,
            NetworkId = networkId,
            Multipath = multipath
        }, cancellationToken).ConfigureAwait(false);

        ZeroTierUdpSocket? udp = null;

        try
        {
            CliOutput.WriteNodeId(socket.NodeId);

            await socket.JoinAsync(cancellationToken).ConfigureAwait(false);
            CliOutput.WriteManagedIps(socket.ManagedIps);

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
                await udp.DisposeAsync().ConfigureAwait(false);
            }

            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }
}
