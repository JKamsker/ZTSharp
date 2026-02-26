using System.Net;
using ZTSharp.ZeroTier.Http;

namespace ZTSharp.Tests;

public sealed class ZeroTierHttpMessageHandlerTests
{
    [Fact]
    public async Task ConnectToResolvedAddressesAsync_TimesOutPerAddress_AndFallsBack()
    {
        var endpoint = new DnsEndPoint("example.invalid", 443);
        var v6 = IPAddress.Parse("2001:db8::1");
        var v4 = IPAddress.Parse("10.0.0.1");

        var calls = new List<IPAddress>();
        using var expectedStream = new MemoryStream(new byte[] { 1, 2, 3 });

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stream = await ZeroTierHttpMessageHandler.ConnectToResolvedAddressesAsync(
            endpoint,
            new[] { v6, v4 },
            connectAsync: async (ep, ct) =>
            {
                calls.Add(ep.Address);
                if (ep.Address.Equals(v6))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                return expectedStream;
            },
            perAddressConnectTimeout: TimeSpan.FromMilliseconds(50),
            cancellationToken: testCts.Token);

        Assert.Same(expectedStream, stream);
        Assert.Equal(new[] { v6, v4 }, calls);
    }
}

