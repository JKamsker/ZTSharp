using System.Net;
using System.Net.Http;
using ZTSharp;
using ZTSharp.Http;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli.Commands;

internal static class CallCommand
{
    public static async Task RunAsync(string[] commandArgs)
    {
        string? statePath = null;
        string? networkText = null;
        string? urlText = null;
        var stack = "managed";
        var transportMode = TransportMode.OsUdp;
        var udpListenPort = 0;
        IPEndPoint? advertisedEndpoint = null;
        var peers = new List<(ulong NodeId, IPEndPoint Endpoint)>();
        var httpMode = "overlay";
        var ipMappings = new List<(IPAddress Address, ulong NodeId)>();

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
                case "--url":
                    urlText = CliParsing.ReadOptionValue(commandArgs, ref i, "--url");
                    break;
                case "--http":
                    httpMode = CliParsing.ReadOptionValue(commandArgs, ref i, "--http");
                    break;
                case "--map-ip":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--map-ip");
                        var mapping = CliParsing.ParseIpMapping(value);
                        ipMappings.Add(mapping);
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

        if (string.IsNullOrWhiteSpace(urlText))
        {
            throw new InvalidOperationException("Missing --url <url>.");
        }

        statePath ??= CliDefaults.CreateTemporaryStatePath();
        var networkId = CliParsing.ParseNetworkId(networkText);
        stack = CliParsing.NormalizeStack(stack);

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url))
        {
            throw new InvalidOperationException("Invalid --url value.");
        }

        using var cancellation = ConsoleCancellation.Create(TimeSpan.FromSeconds(90));

        if (string.Equals(stack, "managed", StringComparison.OrdinalIgnoreCase))
        {
            await RunCallZeroTierAsync(statePath, networkId, url, cancellation.Token).ConfigureAwait(false);
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

            using var httpClient = CreateHttpClient(node, networkId, httpMode, ipMappings);
            var response = await httpClient.GetAsync(url, cancellation.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellation.Token).ConfigureAwait(false);
            Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusCode}");
            Console.WriteLine(body);
        }
        finally
        {
            await node.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunCallZeroTierAsync(string statePath, ulong networkId, Uri url, CancellationToken cancellationToken)
    {
        var socket = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
        {
            StateRootPath = statePath,
            NetworkId = networkId
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            CliOutput.WriteNodeId(socket.NodeId);

            await socket.JoinAsync(cancellationToken).ConfigureAwait(false);
            CliOutput.WriteManagedIps(socket.ManagedIps);

            using var httpClient = socket.CreateHttpClient();
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusCode}");
            Console.WriteLine(body);
        }
        finally
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Handler ownership transfers to HttpClient, which is disposed by the caller.")]
    private static HttpClient CreateHttpClient(
        Node node,
        ulong networkId,
        string httpMode,
        List<(IPAddress Address, ulong NodeId)> ipMappings)
    {
        if (string.Equals(httpMode, "os", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpClient(new SocketsHttpHandler { UseProxy = false });
        }

        if (!string.Equals(httpMode, "overlay", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid --http value (expected overlay|os).");
        }

        OverlayAddressBook? book = null;
        if (ipMappings.Count != 0)
        {
            book = new OverlayAddressBook();
            foreach (var mapping in ipMappings)
            {
                book.Add(mapping.Address, mapping.NodeId);
            }
        }

        var handler = new OverlayHttpMessageHandler(
            node,
            networkId,
            book is null ? null : new OverlayHttpMessageHandlerOptions { AddressBook = book });

        return new HttpClient(handler, disposeHandler: true);
    }
}
