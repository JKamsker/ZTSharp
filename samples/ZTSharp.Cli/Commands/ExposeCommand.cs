using System.Globalization;
using System.Net;
using ZTSharp;
using ZTSharp.Sockets;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli.Commands;

internal static class ExposeCommand
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
        int? overlayListenPort = null;
        (string Host, int Port)? target = null;
        var transportMode = TransportMode.OsUdp;
        var udpListenPort = 0;
        IPEndPoint? advertisedEndpoint = null;
        var peers = new List<(ulong NodeId, IPEndPoint Endpoint)>();

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
                case "--listen":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--listen");
                        overlayListenPort = CliParsing.ParseUShortPort(value, "--listen value");
                        break;
                    }
                case "--to":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--to");
                        target = CliParsing.ParseHostPort(value);
                        break;
                    }
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
                default:
                    throw new InvalidOperationException($"Unknown option '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(networkText))
        {
            throw new InvalidOperationException("Missing --network <nwid>.");
        }

        var networkId = CliParsing.ParseNetworkId(networkText);
        overlayListenPort ??= localPort;
        target ??= ("127.0.0.1", localPort);

        statePath ??= CliDefaults.CreateTemporaryStatePath();

        using var cancellation = ConsoleCancellation.Create();

        stack = CliParsing.NormalizeStack(stack);

        if (string.Equals(stack, "managed", StringComparison.OrdinalIgnoreCase))
        {
            await RunExposeZeroTierAsync(
                    statePath,
                    networkId,
                    overlayListenPort.Value,
                    target.Value.Host,
                    target.Value.Port,
                    cancellation.Token)
                .ConfigureAwait(false);
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
            CliOutput.WriteNodeId(node.NodeId);
            if (localUdp is not null)
            {
                Console.WriteLine($"Local UDP: {localUdp}");
            }

            if (advertisedEndpoint is not null)
            {
                Console.WriteLine($"Advertise UDP: {advertisedEndpoint}");
            }

            Console.WriteLine($"Expose: http://{node.NodeId}:{overlayListenPort}/ -> {target.Value.Host}:{target.Value.Port}");

            var forwarder = new OverlayTcpPortForwarder(
                node,
                networkId,
                overlayListenPort.Value,
                target.Value.Host,
                target.Value.Port);

            try
            {
                await forwarder.RunAsync(cancellation.Token).ConfigureAwait(false);
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

    private static async Task RunExposeZeroTierAsync(
        string statePath,
        ulong networkId,
        int listenPort,
        string targetHost,
        int targetPort,
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

        ZeroTierTcpListener? listener = null;
        using var exposeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exposeToken = exposeCts.Token;
        Task[]? acceptors = null;

        try
        {
            CliOutput.WriteNodeId(socket.NodeId);

            await socket.JoinAsync(exposeToken).ConfigureAwait(false);
            CliOutput.WriteManagedIps(socket.ManagedIps);

            var managedIp = socket.ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (managedIp is null)
            {
                throw new InvalidOperationException("No IPv4 managed IP assigned for this network.");
            }

            listener = await socket.ListenTcpAsync(listenPort, exposeToken).ConfigureAwait(false);

            Console.WriteLine($"Expose: http://{managedIp}:{listenPort}/ -> {targetHost}:{targetPort}");

            var acceptorCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
            acceptors = new Task[acceptorCount];
            var forwarder = new ExposePortForwarder(targetHost, targetPort);

            for (var i = 0; i < acceptors.Length; i++)
            {
                acceptors[i] = forwarder.RunAcceptorAsync(listener, exposeToken);
            }

            await Task.WhenAll(acceptors).ConfigureAwait(false);
        }
        finally
        {
            await exposeCts.CancelAsync().ConfigureAwait(false);

            if (acceptors is not null)
            {
                try
                {
                    await Task.WhenAll(acceptors).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (exposeToken.IsCancellationRequested)
                {
                }
            }

            if (listener is not null)
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }

            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }
}
