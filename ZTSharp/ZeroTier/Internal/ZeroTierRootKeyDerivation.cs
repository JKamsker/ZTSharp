using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierRootKeyDerivation
{
    public static Dictionary<NodeId, byte[]> BuildRootKeys(ZeroTierIdentity localIdentity, ZeroTierWorld planet)
    {
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(planet);

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

        var keys = new Dictionary<NodeId, byte[]>(planet.Roots.Count);
        foreach (var root in planet.Roots)
        {
            var key = new byte[48];
            ZeroTierC25519.Agree(localIdentity.PrivateKey, root.Identity.PublicKey, key);
            keys[root.Identity.NodeId] = key;
        }

        return keys;
    }
}

