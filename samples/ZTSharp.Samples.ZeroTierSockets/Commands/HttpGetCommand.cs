using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ZTSharp.ZeroTier;

namespace ZTSharp.Samples.ZeroTierSockets.Commands;

internal static class HttpGetCommand
{
    public static async Task RunAsync(string[] args)
    {
        string? statePath = null;
        string? networkText = null;
        string? urlText = null;

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
                case "--url":
                    urlText = SampleParsing.ReadOptionValue(args, ref i, "--url");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown option '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(networkText))
        {
            throw new InvalidOperationException("Missing --network <nwid>.");
        }

        if (string.IsNullOrWhiteSpace(urlText) || !Uri.TryCreate(urlText, UriKind.Absolute, out var url))
        {
            throw new InvalidOperationException("Missing/invalid --url <url>.");
        }

        statePath ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-samples", "zerotier-http-get");
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

            using var sockets = new SocketsHttpHandler { UseProxy = false };
            sockets.ConnectCallback = async (context, cancellationToken) =>
            {
                if (!IPAddress.TryParse(context.DnsEndPoint.Host, out var ip))
                {
                    throw new InvalidOperationException("This sample only supports IP literal hosts (no DNS).");
                }

                return await zt
                    .ConnectTcpAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
            };

            using var http = new HttpClient(sockets, disposeHandler: true);
            var body = await http.GetStringAsync(url, token).ConfigureAwait(false);
            Console.WriteLine(body);
        }
        finally
        {
            await zt.DisposeAsync().ConfigureAwait(false);
        }
    }
}

