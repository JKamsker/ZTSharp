using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class ZeroTierSocketFactoryMultipathValidationTests
{
    [Fact]
    public async Task CreateAsync_RejectsDuplicateNonZeroLocalUdpPorts()
    {
        var options = new ZeroTierSocketOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-state-root-"),
            NetworkId = 1,
            Multipath = new ZeroTierMultipathOptions
            {
                Enabled = true,
                UdpSocketCount = 2,
                LocalUdpPorts = new[] { 12345, 12345 }
            }
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ZeroTierSocketFactory.CreateAsync(options, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_MultipathDisabled_AllowsDuplicateNonZeroLocalUdpPorts()
    {
        var options = new ZeroTierSocketOptions
        {
            StateRootPath = TestTempPaths.CreateGuidSuffixed("zt-state-root-"),
            NetworkId = 1,
            Multipath = new ZeroTierMultipathOptions
            {
                Enabled = false,
                UdpSocketCount = 2,
                LocalUdpPorts = new[] { 12345, 12345 }
            }
        };

        await using var socket = await ZeroTierSocketFactory.CreateAsync(options, CancellationToken.None);
        Assert.NotNull(socket);
    }
}
