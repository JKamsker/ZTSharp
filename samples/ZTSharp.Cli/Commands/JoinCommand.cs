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
                default:
                    throw new InvalidOperationException($"Unknown option '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(networkText))
        {
            throw new InvalidOperationException("Missing --network <nwid>.");
        }

        statePath ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-cli", "node-" + Guid.NewGuid().ToString("N"));
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
            await RunJoinZeroTierAsync(statePath, networkId, once, cts.Token).ConfigureAwait(false);
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
            await node.StartAsync(cts.Token).ConfigureAwait(false);
            await node.JoinNetworkAsync(networkId, cts.Token).ConfigureAwait(false);

            if (transportMode == TransportMode.OsUdp && peers.Count != 0)
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

    private static async Task RunJoinZeroTierAsync(string statePath, ulong networkId, bool once, CancellationToken cancellationToken)
    {
        var socket = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
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
}
