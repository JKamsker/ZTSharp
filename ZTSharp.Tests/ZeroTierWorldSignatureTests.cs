using System.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierWorldSignatureTests
{
    [Fact]
    public void SerializeForSign_AndVerifySignature_Succeeds_AndDetectsMutation()
    {
        var signingIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x0102030405);
        Assert.NotNull(signingIdentity.PrivateKey);

        var rootIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x1122334455);
        var roots = new[]
        {
            new ZeroTierWorldRoot(
                rootIdentity,
                new[] { new IPEndPoint(IPAddress.Parse("1.2.3.4"), 9993) })
        };

        var unsignedWorld = new ZeroTierWorld(
            type: ZeroTierWorldType.Planet,
            id: 42,
            timestamp: 1,
            updatesMustBeSignedBy: (byte[])signingIdentity.PublicKey.Clone(),
            signature: new byte[ZeroTierWorld.C25519SignatureLength],
            roots: roots);

        var message = ZeroTierWorldSignature.SerializeForSign(unsignedWorld);
        var signature = ZeroTierC25519.Sign(signingIdentity.PrivateKey!, message);

        var signedWorld = new ZeroTierWorld(
            unsignedWorld.Type,
            unsignedWorld.Id,
            unsignedWorld.Timestamp,
            unsignedWorld.UpdatesMustBeSignedBy,
            signature,
            unsignedWorld.Roots);

        Assert.True(ZeroTierWorldSignature.VerifySignature(signingIdentity.PublicKey, signedWorld));

        var mutatedWorld = new ZeroTierWorld(
            signedWorld.Type,
            signedWorld.Id,
            timestamp: signedWorld.Timestamp + 1,
            signedWorld.UpdatesMustBeSignedBy,
            signedWorld.Signature,
            signedWorld.Roots);

        Assert.False(ZeroTierWorldSignature.VerifySignature(signingIdentity.PublicKey, mutatedWorld));
    }
}

