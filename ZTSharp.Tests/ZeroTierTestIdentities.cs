using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

internal static class ZeroTierTestIdentities
{
    // From external/libzt/ext/ZeroTierOne/selftest.cpp
    public const string KnownGoodIdentity =
        "8e4df28b72:0:ac3d46abe0c21f3cfe7a6c8d6a85cfcffcb82fbd55af6a4d6350657c68200843fa2e16f9418bbd9702cae365f2af5fb4c420908b803a681d4daef6114d78a2d7:bd8dd6e4ce7022d2f812797a80c6ee8ad180dc4ebf301dec8b06d1be08832bddd63a2f1cfa7b2c504474c75bdc8898ba476ef92e8e2d0509f8441985171ff16e";

    public static ZeroTierIdentity CreateFastIdentity(ulong nodeId)
    {
        var privateKey = new byte[ZeroTierIdentity.PrivateKeyLength];
        RandomNumberGenerator.Fill(privateKey);

        var publicKey = new byte[ZeroTierIdentity.PublicKeyLength];

        var xPriv = new X25519PrivateKeyParameters(privateKey, 0);
        var xPub = xPriv.GeneratePublicKey().GetEncoded();
        Buffer.BlockCopy(xPub, 0, publicKey, 0, 32);

        var edPriv = new Ed25519PrivateKeyParameters(privateKey, 32);
        var edPub = edPriv.GeneratePublicKey().GetEncoded();
        Buffer.BlockCopy(edPub, 0, publicKey, 32, 32);

        return new ZeroTierIdentity(new NodeId(nodeId), publicKey, privateKey);
    }
}

