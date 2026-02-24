using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class ZeroTierIdentityTests
{
    // From external/libzt/ext/ZeroTierOne/selftest.cpp
    private const string KnownGoodIdentity =
        "8e4df28b72:0:ac3d46abe0c21f3cfe7a6c8d6a85cfcffcb82fbd55af6a4d6350657c68200843fa2e16f9418bbd9702cae365f2af5fb4c420908b803a681d4daef6114d78a2d7:bd8dd6e4ce7022d2f812797a80c6ee8ad180dc4ebf301dec8b06d1be08832bddd63a2f1cfa7b2c504474c75bdc8898ba476ef92e8e2d0509f8441985171ff16e";

    private const string KnownBadIdentity =
        "9e4df28b72:0:ac3d46abe0c21f3cfe7a6c8d6a85cfcffcb82fbd55af6a4d6350657c68200843fa2e16f9418bbd9702cae365f2af5fb4c420908b803a681d4daef6114d78a2d7:bd8dd6e4ce7022d2f812797a80c6ee8ad180dc4ebf301dec8b06d1be08832bddd63a2f1cfa7b2c504474c75bdc8898ba476ef92e8e2d0509f8441985171ff16e";

    [Fact]
    public void KnownGoodIdentity_LocallyValidates()
    {
        Assert.True(ZeroTierIdentity.TryParse(KnownGoodIdentity, out var identity));
        Assert.Equal(new NodeId(0x8e4df28b72), identity.NodeId);
        Assert.True(identity.LocallyValidate());
    }

    [Fact]
    public void KnownBadIdentity_DoesNotValidate()
    {
        Assert.True(ZeroTierIdentity.TryParse(KnownBadIdentity, out var identity));
        Assert.Equal(new NodeId(0x9e4df28b72), identity.NodeId);
        Assert.False(identity.LocallyValidate());
    }
}

