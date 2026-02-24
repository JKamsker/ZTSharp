using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal static class ZeroTierIdentityHashcash
{
    public const byte HashcashFirstByteLessThan = 17;
    private const int MemoryBytes = 2 * 1024 * 1024;
    private const int BlockSize = 64;

    public static byte[] ComputeMemoryHardHash(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != ZeroTierIdentity.PublicKeyLength)
        {
            throw new ArgumentException($"Public key must be {ZeroTierIdentity.PublicKeyLength} bytes.", nameof(publicKey));
        }

        // Mirrors ZeroTierOne/node/Identity.cpp::_computeMemoryHardHash
        var digest = SHA512.HashData(publicKey);

        var genmem = ArrayPool<byte>.Shared.Rent(MemoryBytes);
        try
        {
            Array.Clear(genmem, 0, MemoryBytes);

            var key = new KeyParameter(digest.AsSpan(0, 32).ToArray());
            var iv = digest.AsSpan(32, 8).ToArray();
            var salsa = new Salsa20Engine();
            salsa.Init(forEncryption: true, new ParametersWithIV(key, iv));

            // Initialize genmem[] using Salsa20 in a CBC-like configuration.
            salsa.ProcessBytes(genmem, 0, BlockSize, genmem, 0);
            for (var offset = BlockSize; offset < MemoryBytes; offset += BlockSize)
            {
                Buffer.BlockCopy(genmem, offset - BlockSize, genmem, offset, BlockSize);
                salsa.ProcessBytes(genmem, offset, BlockSize, genmem, offset);
            }

            // Render final digest using genmem as a lookup table.
            const int digestWords = BlockSize / sizeof(ulong); // 8
            var words = MemoryBytes / sizeof(ulong); // 262144
            for (var i = 0; i < words;)
            {
                var idx1Source = BinaryPrimitives.ReadUInt64BigEndian(genmem.AsSpan(i * sizeof(ulong), sizeof(ulong)));
                i++;
                var idx2Source = BinaryPrimitives.ReadUInt64BigEndian(genmem.AsSpan(i * sizeof(ulong), sizeof(ulong)));
                i++;

                var idx1 = (int)(idx1Source % (ulong)digestWords);
                var idx2 = (int)(idx2Source % (ulong)words);

                var digestOffset = idx1 * sizeof(ulong);
                var genmemOffset = idx2 * sizeof(ulong);

                for (var b = 0; b < sizeof(ulong); b++)
                {
                    (genmem[genmemOffset + b], digest[digestOffset + b]) = (digest[digestOffset + b], genmem[genmemOffset + b]);
                }

                salsa.ProcessBytes(digest, 0, BlockSize, digest, 0);
            }

            return digest;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(genmem);
        }
    }
}

