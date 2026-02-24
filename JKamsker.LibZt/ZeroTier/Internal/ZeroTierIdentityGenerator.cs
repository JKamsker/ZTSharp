using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal static class ZeroTierIdentityGenerator
{
    public static ZeroTierIdentity Generate(CancellationToken cancellationToken = default)
    {
        var privateKey = new byte[ZeroTierIdentity.PrivateKeyLength];
        var publicKey = new byte[ZeroTierIdentity.PublicKeyLength];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RandomNumberGenerator.Fill(privateKey);

            // Calculate Ed25519 public key (bytes 32-63).
            var edPriv = new Ed25519PrivateKeyParameters(privateKey, 32);
            var edPub = edPriv.GeneratePublicKey().GetEncoded();
            Buffer.BlockCopy(edPub, 0, publicKey, 32, 32);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Mimic ZeroTierOne/node/C25519.hpp generateSatisfying():
                //   ++(((uint64_t *)priv)[1]);
                //   --(((uint64_t *)priv)[2]);
                var word1 = BinaryPrimitives.ReadUInt64LittleEndian(privateKey.AsSpan(8, 8));
                word1++;
                BinaryPrimitives.WriteUInt64LittleEndian(privateKey.AsSpan(8, 8), word1);

                var word2 = BinaryPrimitives.ReadUInt64LittleEndian(privateKey.AsSpan(16, 8));
                word2--;
                BinaryPrimitives.WriteUInt64LittleEndian(privateKey.AsSpan(16, 8), word2);

                // Calculate X25519 public key (bytes 0-31).
                var xPriv = new X25519PrivateKeyParameters(privateKey, 0);
                var xPub = xPriv.GeneratePublicKey().GetEncoded();
                Buffer.BlockCopy(xPub, 0, publicKey, 0, 32);

                var digest = ZeroTierIdentityHashcash.ComputeMemoryHardHash(publicKey);
                if (digest[0] >= ZeroTierIdentityHashcash.HashcashFirstByteLessThan)
                {
                    continue;
                }

                var address = ParseAddressFromDigest(digest);
                if (IsReservedAddress(address))
                {
                    break; // restart with a fresh random private key
                }

                return new ZeroTierIdentity(new NodeId(address), (byte[])publicKey.Clone(), (byte[])privateKey.Clone());
            }
        }
    }

    private static ulong ParseAddressFromDigest(byte[] digest)
    {
        if (digest.Length != 64)
        {
            throw new ArgumentException("Digest must be 64 bytes.", nameof(digest));
        }

        // Mirrors Identity.cpp: _address.setTo(digest + 59,ZT_ADDRESS_LENGTH)
        return ((ulong)digest[59] << 32) |
               ((ulong)digest[60] << 24) |
               ((ulong)digest[61] << 16) |
               ((ulong)digest[62] << 8) |
               digest[63];
    }

    private static bool IsReservedAddress(ulong address)
    {
        return address == 0 || (address >> 32) == 0xFF;
    }
}

