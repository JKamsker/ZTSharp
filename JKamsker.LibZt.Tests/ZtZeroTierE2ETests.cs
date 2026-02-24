using System.Globalization;
using JKamsker.LibZt.ZeroTier;

namespace JKamsker.LibZt.Tests;

public sealed class ZtZeroTierE2ETests
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

        await using var socket = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
        {
            StateRootPath = state,
            NetworkId = networkId,
            JoinTimeout = TimeSpan.FromMinutes(1)
        }, cts.Token).ConfigureAwait(false);

        using var http = socket.CreateHttpClient();
        using var response = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
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
}
