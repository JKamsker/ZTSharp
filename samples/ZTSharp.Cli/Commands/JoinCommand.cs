using System.Net;
using ZTSharp;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli.Commands;

internal static class JoinCommand
{
    public static async Task RunAsync(string[] commandArgs)
    {
        string? statePath = null;
        string? networkText = null;
        var stack = "managed";
        var transportMode = TransportMode.OsUdp;
        var udpListenPort = 0;
        IPEndPoint? advertisedEndpoint = null;
        var peers = new List<(ulong NodeId, IPEndPoint Endpoint)>();
        var once = false;

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
                case "--transport":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--transport");
                        transportMode = value switch
                        {
                            "osudp" => TransportMode.OsUdp,
                            "inmem" => TransportMode.InMemory,
                            _ => throw new InvalidOperationException("Invalid --transport value (expected osudp|inmem).")
                        };
                        break;
                    }
                case "--udp-port":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--udp-port");
                        udpListenPort = CliParsing.ParseUShortPortAllowZero(value, "--udp-port value");
                        break;
                    }
                case "--advertise":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--advertise");
                        advertisedEndpoint = CliParsing.ParseIpEndpoint(value);
                        break;
                    }
                case "--peer":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--peer");
                        var parsed = CliParsing.ParsePeer(value);
                        peers.Add(parsed);
                        break;
                    }
                case "--once":
                    once = true;
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

        statePath ??= CliDefaults.CreateTemporaryStatePath();
        var networkId = CliParsing.ParseNetworkId(networkText);
        stack = CliParsing.NormalizeStack(stack);

        using var cancellation = ConsoleCancellation.Create();

        if (string.Equals(stack, "managed", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedUdpSocketCount = mpUdpSockets ?? mpUdpPorts?.Count ?? 1;
            if (mpUdpPorts is not null && mpUdpPorts.Count != resolvedUdpSocketCount)
            {
                throw new InvalidOperationException("Invalid multipath config: --mp-udp-ports count must match --mp-udp-sockets.");
            }

            var multipath = new ZeroTierMultipathOptions
            {
                Enabled = mpEnabled,
                BondPolicy = mpBondPolicy,
                UdpSocketCount = resolvedUdpSocketCount,
                LocalUdpPorts = mpUdpPorts,
                WarmupDuplicateToRoot = mpWarmupRoot
            };

            await RunJoinZeroTierAsync(statePath, networkId, multipath, once, cancellation.Token).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(stack, "overlay", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid --stack value (expected managed|overlay).");
        }

        var node = new Node(new NodeOptions
        {
            StateRootPath = statePath,
            TransportMode = transportMode,
            UdpListenPort = transportMode == TransportMode.OsUdp ? udpListenPort : null,
            EnablePeerDiscovery = true,
            AdvertisedTransportEndpoint = advertisedEndpoint
        });

        try
        {
            await node.StartAsync(cancellation.Token).ConfigureAwait(false);
            await node.JoinNetworkAsync(networkId, cancellation.Token).ConfigureAwait(false);

            if (transportMode == TransportMode.OsUdp && peers.Count != 0)
            {
                foreach (var peer in peers)
                {
                    await node.AddPeerAsync(networkId, peer.NodeId, peer.Endpoint, cancellation.Token).ConfigureAwait(false);
                }
            }

            var localUdp = node.LocalTransportEndpoint;
            Console.WriteLine($"State: {statePath}");
            CliOutput.WriteNodeId(node.NodeId);
            if (localUdp is not null)
            {
                Console.WriteLine($"Local UDP: {localUdp}");
            }

            if (once)
            {
                return;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            await node.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunJoinZeroTierAsync(
        string statePath,
        ulong networkId,
        ZeroTierMultipathOptions multipath,
        bool once,
        CancellationToken cancellationToken)
    {
        var socket = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
        {
            StateRootPath = statePath,
            NetworkId = networkId,
            Multipath = multipath
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            Console.WriteLine($"State: {statePath}");
            CliOutput.WriteNodeId(socket.NodeId);

            await socket.JoinAsync(cancellationToken).ConfigureAwait(false);
            CliOutput.WriteManagedIps(socket.ManagedIps);

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
}
