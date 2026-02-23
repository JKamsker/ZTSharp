using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZtZeroTierC25519
{
    private const int PrivateKeyLength = 64;
    private const int PublicKeyLength = 64;
    private const int DhKeyLength = 32;
    private const int EdKeyOffset = 32;
    private const int EdKeyLength = 32;
    private const int SignatureLength = 96;
    private const int DigestPrefixLength = 32;

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

    public static byte[] Sign(ReadOnlySpan<byte> myPrivateKey, ReadOnlySpan<byte> message)
    {
        if (myPrivateKey.Length != PrivateKeyLength)
        {
            throw new ArgumentException($"Private key must be {PrivateKeyLength} bytes.", nameof(myPrivateKey));
        }

        var digest = SHA512.HashData(message);
        var digestPrefix = digest.AsSpan(0, DigestPrefixLength).ToArray();

        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(myPrivateKey.Slice(EdKeyOffset, EdKeyLength).ToArray(), 0));
        signer.BlockUpdate(digestPrefix, 0, digestPrefix.Length);
        var signature64 = signer.GenerateSignature();

        var signature96 = new byte[SignatureLength];
        signature64.CopyTo(signature96, 0);
        digestPrefix.CopyTo(signature96, 64);
        return signature96;
    }

    public static bool VerifySignature(ReadOnlySpan<byte> theirPublicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (theirPublicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException($"Public key must be {PublicKeyLength} bytes.", nameof(theirPublicKey));
        }

        if (signature.Length != SignatureLength)
        {
            throw new ArgumentException($"Signature must be {SignatureLength} bytes.", nameof(signature));
        }

        var digest = SHA512.HashData(message);
        var digestPrefix = digest.AsSpan(0, DigestPrefixLength);

        if (!CryptographicOperations.FixedTimeEquals(signature.Slice(64, DigestPrefixLength), digestPrefix))
        {
            return false;
        }

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(theirPublicKey.Slice(EdKeyOffset, EdKeyLength).ToArray(), 0));
        verifier.BlockUpdate(digestPrefix.ToArray(), 0, digestPrefix.Length);
        return verifier.VerifySignature(signature.Slice(0, 64).ToArray());
    }
}
