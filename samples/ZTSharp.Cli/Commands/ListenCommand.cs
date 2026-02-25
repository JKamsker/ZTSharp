using System.Net.Sockets;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli.Commands;

internal static class ListenCommand
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
        long bodyBytes = 0;

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
                case "--body-bytes":
                    {
                        var value = CliParsing.ReadOptionValue(commandArgs, ref i, "--body-bytes");
                        bodyBytes = CliParsing.ParseNonNegativeLong(value, "--body-bytes value");

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
            await RunListenZeroTierAsync(statePath, networkId, localPort, bodyBytes, cancellation.Token).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("Invalid --stack value (expected managed).");
    }

    private static async Task RunListenZeroTierAsync(
        string statePath,
        ulong networkId,
        int listenPort,
        long bodyBytes,
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

        ZeroTierTcpListener? listener4 = null;
        ZeroTierTcpListener? listener6 = null;
        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listenToken = listenCts.Token;
        List<Task>? acceptors = null;

        try
        {
            Console.WriteLine($"NodeId: {socket.NodeId}");

            await socket.JoinAsync(listenToken).ConfigureAwait(false);
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

            var acceptorCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
            acceptors = new List<Task>(acceptorCount * 2);
            var server = new ListenHttpServer(bodyBytes);

            if (managedIp4 is not null)
            {
                listener4 = await socket.ListenTcpAsync(managedIp4, listenPort, listenToken).ConfigureAwait(false);
                Console.WriteLine($"Listen: http://{managedIp4}:{listenPort}/");

                for (var i = 0; i < acceptorCount; i++)
                {
                    acceptors.Add(server.RunAcceptorAsync(listener4, listenToken));
                }
            }

            if (managedIp6 is not null)
            {
                listener6 = await socket.ListenTcpAsync(managedIp6, listenPort, listenToken).ConfigureAwait(false);
                Console.WriteLine($"Listen: http://[{managedIp6}]:{listenPort}/");

                for (var i = 0; i < acceptorCount; i++)
                {
                    acceptors.Add(server.RunAcceptorAsync(listener6, listenToken));
                }
            }

            await Task.WhenAll(acceptors).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await listenCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }

            if (acceptors is not null && acceptors.Count != 0)
            {
                try
                {
                    await Task.WhenAll(acceptors).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (listenToken.IsCancellationRequested)
                {
                }
            }

            if (listener4 is not null)
            {
                try
                {
                    await listener4.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (listener6 is not null)
            {
                try
                {
                    await listener6.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }
}
