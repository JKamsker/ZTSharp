using System.Globalization;
using System.Net;
using JKamsker.LibZt.ZeroTier;

namespace JKamsker.LibZt.Tests;

public sealed class ZeroTierE2ETests
{
    [ZeroTierE2eFact("LIBZT_ZEROTIER_NWID")]
    public async Task ZeroTier_JoinAndHttpGet_E2E()
    {
        var networkText = Environment.GetEnvironmentVariable("LIBZT_ZEROTIER_NWID");
        if (string.IsNullOrWhiteSpace(networkText))
        {
            throw new InvalidOperationException("Missing LIBZT_ZEROTIER_NWID.");
        }

        var urlText = Environment.GetEnvironmentVariable("LIBZT_ZEROTIER_URL");
        if (string.IsNullOrWhiteSpace(urlText))
        {
            throw new InvalidOperationException("Missing LIBZT_ZEROTIER_URL.");
        }

        var state = Environment.GetEnvironmentVariable("LIBZT_ZEROTIER_STATE");
        state ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-zerotier-e2e", Guid.NewGuid().ToString("N"));

        var networkId = ParseNetworkId(networkText);
        var url = new Uri(urlText, UriKind.Absolute);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await using var socket = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
        {
            StateRootPath = state,
            NetworkId = networkId,
            JoinTimeout = TimeSpan.FromMinutes(1)
        }, cts.Token).ConfigureAwait(false);

        using var http = socket.CreateHttpClient();
        using var response = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    [ZeroTierE2eFact("LIBZT_ZEROTIER_URL_V6")]
    public async Task ZeroTier_JoinAndHttpGet_IPv6_E2E()
    {
        var networkText = Environment.GetEnvironmentVariable("LIBZT_ZEROTIER_NWID");
        if (string.IsNullOrWhiteSpace(networkText))
        {
            throw new InvalidOperationException("Missing LIBZT_ZEROTIER_NWID.");
        }

        var urlText = Environment.GetEnvironmentVariable("LIBZT_ZEROTIER_URL_V6");
        if (string.IsNullOrWhiteSpace(urlText))
        {
            throw new InvalidOperationException("Missing LIBZT_ZEROTIER_URL_V6.");
        }

        var state = Environment.GetEnvironmentVariable("LIBZT_ZEROTIER_STATE");
        state ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-zerotier-e2e", Guid.NewGuid().ToString("N"));

        var networkId = ParseNetworkId(networkText);
        var url = new Uri(urlText, UriKind.Absolute);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await using var socket = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
        {
            StateRootPath = state,
            NetworkId = networkId,
            JoinTimeout = TimeSpan.FromMinutes(1)
        }, cts.Token).ConfigureAwait(false);

        using var http = socket.CreateHttpClient();
        using var response = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    [ZeroTierE2eFact("LIBZT_ZEROTIER_UDP_ECHO_V6")]
    public async Task ZeroTier_UdpEcho_IPv6_E2E()
    {
        var networkText = Environment.GetEnvironmentVariable("LIBZT_ZEROTIER_NWID");
        if (string.IsNullOrWhiteSpace(networkText))
        {
            throw new InvalidOperationException("Missing LIBZT_ZEROTIER_NWID.");
        }

        var echoText = Environment.GetEnvironmentVariable("LIBZT_ZEROTIER_UDP_ECHO_V6");
        if (string.IsNullOrWhiteSpace(echoText))
        {
            throw new InvalidOperationException("Missing LIBZT_ZEROTIER_UDP_ECHO_V6.");
        }

        var state = Environment.GetEnvironmentVariable("LIBZT_ZEROTIER_STATE");
        state ??= Path.Combine(Path.GetTempPath(), "libzt-dotnet-zerotier-e2e", Guid.NewGuid().ToString("N"));

        var networkId = ParseNetworkId(networkText);
        var echoEndpoint = ParseIpEndPoint(echoText);
        if (echoEndpoint.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new InvalidOperationException($"Expected LIBZT_ZEROTIER_UDP_ECHO_V6 to be an IPv6 endpoint (got '{echoEndpoint}').");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await using var socket = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
        {
            StateRootPath = state,
            NetworkId = networkId,
            JoinTimeout = TimeSpan.FromMinutes(1)
        }, cts.Token).ConfigureAwait(false);

        await socket.JoinAsync(cts.Token).ConfigureAwait(false);

        var localV6 = socket.ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                      ?? throw new InvalidOperationException("No IPv6 managed IP assigned for this network.");

        await using var udp = await socket.BindUdpAsync(localAddress: localV6, port: 0, cancellationToken: cts.Token).ConfigureAwait(false);

        await udp.SendToAsync("ping"u8.ToArray(), echoEndpoint, cts.Token).ConfigureAwait(false);

        var buffer = new byte[64];
        var received = await udp.ReceiveFromAsync(buffer, timeout: TimeSpan.FromSeconds(10), cancellationToken: cts.Token).ConfigureAwait(false);
        var payload = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
        Assert.Equal("pong"u8.ToArray(), payload);
        Assert.Equal(echoEndpoint.Address, received.RemoteEndPoint.Address);
        Assert.Equal(echoEndpoint.Port, received.RemoteEndPoint.Port);
    }

    private static ulong ParseNetworkId(string text)
    {
        var span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span.Slice(2);
        }

        if (!ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var networkId) || networkId == 0)
        {
            throw new InvalidOperationException("Invalid LIBZT_ZEROTIER_NWID (expected hex, e.g. 0x9ad07d01093a69e3).");
        }

        return networkId;
    }

    private static IPEndPoint ParseIpEndPoint(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('['))
        {
            var endBracket = value.IndexOf(']');
            if (endBracket <= 1 || endBracket + 1 >= value.Length || value[endBracket + 1] != ':')
            {
                throw new InvalidOperationException($"Invalid endpoint '{text}' (expected [ipv6]:port).");
            }

            var host = value.Substring(1, endBracket - 1);
            var portText = value.Substring(endBracket + 2);
            if (!IPAddress.TryParse(host, out var ip))
            {
                throw new InvalidOperationException($"Invalid endpoint host '{host}'.");
            }

            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > ushort.MaxValue)
            {
                throw new InvalidOperationException($"Invalid endpoint port '{portText}'.");
            }

            return new IPEndPoint(ip, port);
        }

        var lastColon = value.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == value.Length - 1)
        {
            throw new InvalidOperationException($"Invalid endpoint '{text}' (expected ip:port).");
        }

        var hostPart = value.Substring(0, lastColon);
        var portPart = value.Substring(lastColon + 1);
        if (!IPAddress.TryParse(hostPart, out var ip2))
        {
            throw new InvalidOperationException($"Invalid endpoint host '{hostPart}'.");
        }

        if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out var port2) || port2 is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Invalid endpoint port '{portPart}'.");
        }

        return new IPEndPoint(ip2, port2);
    }
}
