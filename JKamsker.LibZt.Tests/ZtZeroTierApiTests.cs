using System.Net;
using JKamsker.LibZt.ZeroTier;

namespace JKamsker.LibZt.Tests;

public sealed class ZtZeroTierApiTests
{
    [Fact]
    public async Task CreateAsync_Throws_ForMissingStateRootPath()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            _ = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
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
            _ = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
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
            _ = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
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
            _ = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
            {
                StateRootPath = "x",
                NetworkId = 1,
                PlanetSource = (ZtZeroTierPlanetSource)123
            });
        });
    }

    [Fact]
    public async Task ConnectTcpAsync_NotSupported_Yet()
    {
        await using var socket = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
        {
            StateRootPath = Path.Combine(Path.GetTempPath(), "zt-test-" + Guid.NewGuid()),
            NetworkId = 1
        });

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            _ = await socket.ConnectTcpAsync(new IPEndPoint(IPAddress.Loopback, 80)).ConfigureAwait(false);
        });
    }
}
