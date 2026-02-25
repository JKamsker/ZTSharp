using System.Net;
using ZTSharp.ZeroTier;

namespace ZTSharp.Tests;

public sealed class ZeroTierApiTests
{
    [Fact]
    public async Task CreateAsync_Throws_ForMissingStateRootPath()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            _ = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
            {
                StateRootPath = "",
                NetworkId = 1
            });
        });
    }

    [Fact]
    public async Task CreateAsync_Throws_ForNetworkIdZero()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            _ = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
            {
                StateRootPath = "x",
                NetworkId = 0
            });
        });
    }

    [Fact]
    public async Task CreateAsync_Throws_ForNonPositiveJoinTimeout()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            _ = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
            {
                StateRootPath = "x",
                NetworkId = 1,
                JoinTimeout = TimeSpan.Zero
            });
        });
    }

    [Fact]
    public async Task CreateAsync_Throws_ForInvalidPlanetSource()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            _ = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
            {
                StateRootPath = "x",
                NetworkId = 1,
                PlanetSource = (ZeroTierPlanetSource)123
            });
        });
    }

    [Fact]
    public async Task ConnectTcpAsync_Throws_ForIpv6Remote()
    {
        await using var socket = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-test-"),
            NetworkId = 1
        });

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            _ = await socket.ConnectTcpAsync(new IPEndPoint(IPAddress.IPv6Loopback, 80)).ConfigureAwait(false);
        });
    }
}
