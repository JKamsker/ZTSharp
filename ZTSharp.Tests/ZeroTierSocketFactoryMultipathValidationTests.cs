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
}

