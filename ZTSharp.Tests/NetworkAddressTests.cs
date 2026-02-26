using System.Net;

namespace ZTSharp.Tests;

public sealed class NetworkAddressTests
{
    [Fact]
    public async Task NetworkAddresses_RoundTrip()
    {
        var networkId = 424242UL;

        await using var node = new Node(new NodeOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-node-"),
            StateStore = new MemoryStateStore()
        });

        await node.StartAsync();
        await node.JoinNetworkAsync(networkId);

        var expected = new[]
        {
            new NetworkAddress(IPAddress.Parse("10.10.0.1"), 24),
            new NetworkAddress(IPAddress.Parse("fd00::1"), 64)
        };

        await node.SetNetworkAddressesAsync(networkId, expected);
        var actual = await node.GetNetworkAddressesAsync(networkId);

        Assert.Equal(expected.Length, actual.Count);
        Assert.Equal(expected[0], actual[0]);
        Assert.Equal(expected[1], actual[1]);
    }
}
