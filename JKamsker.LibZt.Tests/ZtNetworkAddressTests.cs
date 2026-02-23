using System.Net;

namespace JKamsker.LibZt.Tests;

public sealed class ZtNetworkAddressTests
{
    [Fact]
    public async Task NetworkAddresses_RoundTrip()
    {
        var networkId = 424242UL;

        await using var node = new ZtNode(new ZtNodeOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-node-" + Guid.NewGuid()),
            StateStore = new MemoryZtStateStore()
        });

        await node.StartAsync();
        await node.JoinNetworkAsync(networkId);

        var expected = new[]
        {
            new ZtNetworkAddress(IPAddress.Parse("10.10.0.1"), 24),
            new ZtNetworkAddress(IPAddress.Parse("fd00::1"), 64)
        };

        await node.SetNetworkAddressesAsync(networkId, expected);
        var actual = await node.GetNetworkAddressesAsync(networkId);

        Assert.Equal(expected.Length, actual.Count);
        Assert.Equal(expected[0], actual[0]);
        Assert.Equal(expected[1], actual[1]);
    }
}

