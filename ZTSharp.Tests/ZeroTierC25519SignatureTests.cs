using System.Text;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierC25519SignatureTests
{
    // From external/libzt/ext/ZeroTierOne/selftest.cpp
    private const string KnownGoodIdentity =
        "8e4df28b72:0:ac3d46abe0c21f3cfe7a6c8d6a85cfcffcb82fbd55af6a4d6350657c68200843fa2e16f9418bbd9702cae365f2af5fb4c420908b803a681d4daef6114d78a2d7:bd8dd6e4ce7022d2f812797a80c6ee8ad180dc4ebf301dec8b06d1be08832bddd63a2f1cfa7b2c504474c75bdc8898ba476ef92e8e2d0509f8441985171ff16e";

    [Fact]
    public void Sign_Verify_RoundTrips()
    {
        Assert.True(ZeroTierIdentity.TryParse(KnownGoodIdentity, out var identity));
        Assert.NotNull(identity.PrivateKey);

        var message = Encoding.UTF8.GetBytes("hello world");
        var signature = ZeroTierC25519.Sign(identity.PrivateKey!, message);

        Assert.True(ZeroTierC25519.VerifySignature(identity.PublicKey, message, signature));

        message[0] ^= 0x01;
        Assert.False(ZeroTierC25519.VerifySignature(identity.PublicKey, message, signature));
    }
}

