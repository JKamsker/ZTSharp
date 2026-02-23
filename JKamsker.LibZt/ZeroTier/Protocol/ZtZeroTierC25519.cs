using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZtZeroTierC25519
{
    private const int PrivateKeyLength = 64;
    private const int PublicKeyLength = 64;
    private const int DhKeyLength = 32;

    public static void Agree(ReadOnlySpan<byte> myPrivateKey, ReadOnlySpan<byte> theirPublicKey, Span<byte> destination)
    {
        if (myPrivateKey.Length != PrivateKeyLength)
        {
            throw new ArgumentException($"Private key must be {PrivateKeyLength} bytes.", nameof(myPrivateKey));
        }

        if (theirPublicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException($"Public key must be {PublicKeyLength} bytes.", nameof(theirPublicKey));
        }

        if (destination.IsEmpty)
        {
            return;
        }

        var priv = new X25519PrivateKeyParameters(myPrivateKey.Slice(0, DhKeyLength).ToArray(), 0);
        var pub = new X25519PublicKeyParameters(theirPublicKey.Slice(0, DhKeyLength).ToArray(), 0);

        var agreement = new X25519Agreement();
        agreement.Init(priv);

        var rawKey = new byte[DhKeyLength];
        agreement.CalculateAgreement(pub, rawKey, 0);

        var digest = SHA512.HashData(rawKey);
        var i = 0;
        var k = 0;
        while (i < destination.Length)
        {
            if (k == digest.Length)
            {
                digest = SHA512.HashData(digest);
                k = 0;
            }

            destination[i++] = digest[k++];
        }
    }
}
