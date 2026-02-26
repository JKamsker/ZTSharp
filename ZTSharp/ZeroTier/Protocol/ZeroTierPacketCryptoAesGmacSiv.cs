using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierPacketCryptoAesGmacSiv
{
    private const byte KbkdfLabelAesGmacSivK0 = (byte)'0';
    private const byte KbkdfLabelAesGmacSivK1 = (byte)'1';

    private const int AesGmacSivNonceLength = 12;
    private const int AesGmacSivTagLength = 16;
    private const int AesGmacSivAadPaddedLength = 16;
    private const int AesBlockLength = 16;

    public static void Armor(Span<byte> packet, ReadOnlySpan<byte> key48)
    {
        if (key48.Length != ZeroTierPacketCrypto.SymmetricKeyLength)
        {
            throw new ArgumentException(
                $"Key must be exactly {ZeroTierPacketCrypto.SymmetricKeyLength} bytes for AES-GMAC-SIV.",
                nameof(key48));
        }

        ZeroTierPacketCrypto.SetCipher(packet, ZeroTierPacketCrypto.CipherAesGmacSiv);

        Span<byte> k0 = stackalloc byte[ZeroTierPacketCrypto.KeyLength];
        Span<byte> k1 = stackalloc byte[ZeroTierPacketCrypto.KeyLength];
        DeriveAesGmacSivKeys(key48, k0, k1);

        var payloadLength = packet.Length - ZeroTierPacketHeader.IndexVerb;

        Span<byte> nonce = stackalloc byte[AesGmacSivNonceLength];
        packet.Slice(0, 8).CopyTo(nonce);
        nonce.Slice(8, 4).Clear();

        var authData = new byte[AesGmacSivAadPaddedLength + payloadLength];
        packet.Slice(8, 10).CopyTo(authData.AsSpan(0, 10));
        authData[10] = (byte)(packet[ZeroTierPacketHeader.IndexFlags] & 0xF8);
        packet.Slice(ZeroTierPacketHeader.IndexVerb, payloadLength).CopyTo(authData.AsSpan(AesGmacSivAadPaddedLength));

        Span<byte> gmacTag = stackalloc byte[AesGmacSivTagLength];
        ComputeGmacTag(k0, nonce, authData, gmacTag);

        Span<byte> reducedTag = stackalloc byte[8];
        for (var i = 0; i < reducedTag.Length; i++)
        {
            reducedTag[i] = (byte)(gmacTag[i] ^ gmacTag[i + 8]);
        }

        Span<byte> ivMac = stackalloc byte[AesBlockLength];
        packet.Slice(0, 8).CopyTo(ivMac);
        reducedTag.CopyTo(ivMac.Slice(8, 8));

        using var aes = Aes.Create();
        aes.Key = k1.ToArray();

        Span<byte> encryptedIvMac = stackalloc byte[AesBlockLength];
        aes.EncryptEcb(ivMac, encryptedIvMac, PaddingMode.None);

        encryptedIvMac.Slice(0, 8).CopyTo(packet.Slice(0, 8));
        encryptedIvMac.Slice(8, 8).CopyTo(packet.Slice(ZeroTierPacketHeader.IndexMac, 8));

        Span<byte> ctrIv = stackalloc byte[AesBlockLength];
        encryptedIvMac.CopyTo(ctrIv);
        ctrIv[12] &= 0x7F;
        AesCtrXorInPlace(aes, ctrIv, packet.Slice(ZeroTierPacketHeader.IndexVerb, payloadLength));
    }

    public static bool Dearmor(Span<byte> packet, ReadOnlySpan<byte> key48)
    {
        if (key48.Length != ZeroTierPacketCrypto.SymmetricKeyLength)
        {
            throw new ArgumentException(
                $"Key must be exactly {ZeroTierPacketCrypto.SymmetricKeyLength} bytes for AES-GMAC-SIV.",
                nameof(key48));
        }

        Span<byte> k0 = stackalloc byte[ZeroTierPacketCrypto.KeyLength];
        Span<byte> k1 = stackalloc byte[ZeroTierPacketCrypto.KeyLength];
        DeriveAesGmacSivKeys(key48, k0, k1);

        Span<byte> encryptedIvMac = stackalloc byte[AesBlockLength];
        packet.Slice(0, 8).CopyTo(encryptedIvMac);
        packet.Slice(ZeroTierPacketHeader.IndexMac, 8).CopyTo(encryptedIvMac.Slice(8, 8));

        using var aes = Aes.Create();
        aes.Key = k1.ToArray();

        Span<byte> ivMac = stackalloc byte[AesBlockLength];
        aes.DecryptEcb(encryptedIvMac, ivMac, PaddingMode.None);

        Span<byte> ctrIv = stackalloc byte[AesBlockLength];
        encryptedIvMac.CopyTo(ctrIv);
        ctrIv[12] &= 0x7F;

        var payloadLength = packet.Length - ZeroTierPacketHeader.IndexVerb;
        var rented = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            var plaintextPayload = rented.AsSpan(0, payloadLength);
            packet.Slice(ZeroTierPacketHeader.IndexVerb, payloadLength).CopyTo(plaintextPayload);
            AesCtrXorInPlace(aes, ctrIv, plaintextPayload);

            Span<byte> nonce = stackalloc byte[AesGmacSivNonceLength];
            ivMac.Slice(0, 8).CopyTo(nonce);
            nonce.Slice(8, 4).Clear();

            var authData = new byte[AesGmacSivAadPaddedLength + payloadLength];
            packet.Slice(8, 10).CopyTo(authData.AsSpan(0, 10));
            authData[10] = (byte)(packet[ZeroTierPacketHeader.IndexFlags] & 0xF8);
            plaintextPayload.CopyTo(authData.AsSpan(AesGmacSivAadPaddedLength));

            Span<byte> gmacTag = stackalloc byte[AesGmacSivTagLength];
            ComputeGmacTag(k0, nonce, authData, gmacTag);

            Span<byte> reducedTag = stackalloc byte[8];
            for (var i = 0; i < reducedTag.Length; i++)
            {
                reducedTag[i] = (byte)(gmacTag[i] ^ gmacTag[i + 8]);
            }

            if (!CryptographicOperations.FixedTimeEquals(reducedTag, ivMac.Slice(8, 8)))
            {
                return false;
            }

            plaintextPayload.CopyTo(packet.Slice(ZeroTierPacketHeader.IndexVerb, payloadLength));
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented.AsSpan(0, payloadLength));
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    private static void ComputeGmacTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (key.Length != ZeroTierPacketCrypto.KeyLength)
        {
            throw new ArgumentException($"Key must be {ZeroTierPacketCrypto.KeyLength} bytes.", nameof(key));
        }

        if (nonce.Length != AesGmacSivNonceLength)
        {
            throw new ArgumentException($"Nonce must be {AesGmacSivNonceLength} bytes.", nameof(nonce));
        }

        if (destination.Length < AesGmacSivTagLength)
        {
            throw new ArgumentException($"Destination must be at least {AesGmacSivTagLength} bytes.", nameof(destination));
        }

        using var gcm = new AesGcm(key, AesGmacSivTagLength);
        gcm.Encrypt(
            nonce,
            plaintext: ReadOnlySpan<byte>.Empty,
            ciphertext: Span<byte>.Empty,
            tag: destination.Slice(0, AesGmacSivTagLength),
            associatedData: data);
    }

    private static void DeriveAesGmacSivKeys(ReadOnlySpan<byte> key48, Span<byte> k0, Span<byte> k1)
    {
        if (key48.Length != ZeroTierPacketCrypto.SymmetricKeyLength)
        {
            throw new ArgumentException($"Key must be exactly {ZeroTierPacketCrypto.SymmetricKeyLength} bytes.", nameof(key48));
        }

        if (k0.Length != ZeroTierPacketCrypto.KeyLength)
        {
            throw new ArgumentException($"k0 must be {ZeroTierPacketCrypto.KeyLength} bytes.", nameof(k0));
        }

        if (k1.Length != ZeroTierPacketCrypto.KeyLength)
        {
            throw new ArgumentException($"k1 must be {ZeroTierPacketCrypto.KeyLength} bytes.", nameof(k1));
        }

        Span<byte> derived = stackalloc byte[ZeroTierPacketCrypto.SymmetricKeyLength];

        KbkdfHmacSha384(key48, label: KbkdfLabelAesGmacSivK0, context: 0, iter: 0, destination: derived);
        derived.Slice(0, ZeroTierPacketCrypto.KeyLength).CopyTo(k0);

        KbkdfHmacSha384(key48, label: KbkdfLabelAesGmacSivK1, context: 0, iter: 0, destination: derived);
        derived.Slice(0, ZeroTierPacketCrypto.KeyLength).CopyTo(k1);
    }

    private static void KbkdfHmacSha384(
        ReadOnlySpan<byte> key48,
        byte label,
        byte context,
        uint iter,
        Span<byte> destination)
    {
        if (key48.Length != ZeroTierPacketCrypto.SymmetricKeyLength)
        {
            throw new ArgumentException($"Key must be exactly {ZeroTierPacketCrypto.SymmetricKeyLength} bytes.", nameof(key48));
        }

        if (destination.Length < ZeroTierPacketCrypto.SymmetricKeyLength)
        {
            throw new ArgumentException($"Destination must be at least {ZeroTierPacketCrypto.SymmetricKeyLength} bytes.", nameof(destination));
        }

        Span<byte> msg = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(msg.Slice(0, 4), iter);

        msg[4] = (byte)'Z';
        msg[5] = (byte)'T';
        msg[6] = label;
        msg[7] = 0;
        msg[8] = context;
        msg[9] = 0;
        msg[10] = 0;
        msg[11] = 0x01;
        msg[12] = 0x80;

        if (!HMACSHA384.TryHashData(key48, msg, destination.Slice(0, ZeroTierPacketCrypto.SymmetricKeyLength), out var written)
            || written != ZeroTierPacketCrypto.SymmetricKeyLength)
        {
            throw new CryptographicException("KBKDF failed.");
        }
    }

    private static void AesCtrXorInPlace(Aes aes, Span<byte> counter, Span<byte> data)
    {
        if (counter.Length != AesBlockLength)
        {
            throw new ArgumentException($"Counter must be {AesBlockLength} bytes.", nameof(counter));
        }

        Span<byte> keystream = stackalloc byte[AesBlockLength];
        for (var offset = 0; offset < data.Length; offset += AesBlockLength)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);

            var blockLength = Math.Min(AesBlockLength, data.Length - offset);
            for (var i = 0; i < blockLength; i++)
            {
                data[offset + i] ^= keystream[i];
            }

            IncrementCounter32(counter);
        }
    }

    private static void IncrementCounter32(Span<byte> counter)
    {
        for (var i = 15; i >= 12; i--)
        {
            counter[i]++;
            if (counter[i] != 0)
            {
                break;
            }
        }
    }
}
