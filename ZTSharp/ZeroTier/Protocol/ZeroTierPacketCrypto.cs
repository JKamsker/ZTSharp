using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierPacketCrypto
{
    private const int KeyLength = 32;
    private const int SymmetricKeyLength = 48;
    private const int MangledKeyLength = 32;
    private const int MacKeyLength = 32;
    private const int DiscardedKeystreamBytes = 64;
    private const int MacLength = 8;

    private const int IndexFlags = 18;
    private const int IndexMac = 19;
    private const int IndexVerb = 27;

    private const byte CipherC25519Poly1305None = 0;
    private const byte CipherC25519Poly1305Salsa2012 = 1;
    private const byte CipherAesGmacSiv = 3;

    private const byte KbkdfLabelAesGmacSivK0 = (byte)'0';
    private const byte KbkdfLabelAesGmacSivK1 = (byte)'1';

    private const int AesGmacSivNonceLength = 12;
    private const int AesGmacSivTagLength = 16;
    private const int AesGmacSivAadPaddedLength = 16;
    private const int AesBlockLength = 16;

    public static void Armor(Span<byte> packet, ReadOnlySpan<byte> key, bool encryptPayload)
    {
        if (packet.Length < ZeroTierPacketHeader.Length)
        {
            throw new ArgumentException("Packet is too short.", nameof(packet));
        }

        if (key.Length < KeyLength)
        {
            throw new ArgumentException($"Key must be at least {KeyLength} bytes.", nameof(key));
        }

        if (encryptPayload && key.Length >= SymmetricKeyLength)
        {
            ArmorAesGmacSiv(packet, key.Slice(0, SymmetricKeyLength));
            return;
        }

        SetCipher(packet, encryptPayload ? CipherC25519Poly1305Salsa2012 : CipherC25519Poly1305None);

        Span<byte> mangledKey = stackalloc byte[MangledKeyLength];
        MangleKey(key.Slice(0, KeyLength), packet, mangledKey);

        var payloadLength = packet.Length - IndexVerb;
        var keystreamLength = DiscardedKeystreamBytes + (encryptPayload ? payloadLength : 0);
        var keystream = new byte[keystreamLength];
        ZeroTierSalsa20.GenerateKeyStream12(mangledKey, packet.Slice(0, 8), keystream);

        if (encryptPayload)
        {
            XorInPlace(packet.Slice(IndexVerb, payloadLength), keystream.AsSpan(DiscardedKeystreamBytes));
        }

        Span<byte> tag = stackalloc byte[16];
        ComputePoly1305Tag(packet.Slice(IndexVerb, payloadLength), keystream.AsSpan(0, MacKeyLength), tag);
        tag.Slice(0, MacLength).CopyTo(packet.Slice(IndexMac, MacLength));
    }

    public static bool Dearmor(Span<byte> packet, ReadOnlySpan<byte> key)
    {
        if (packet.Length < ZeroTierPacketHeader.Length)
        {
            return false;
        }

        if (key.Length < KeyLength)
        {
            throw new ArgumentException($"Key must be at least {KeyLength} bytes.", nameof(key));
        }

        var cipher = (byte)((packet[IndexFlags] & 0x38) >> 3);
        if (cipher == CipherAesGmacSiv)
        {
            if (key.Length < SymmetricKeyLength)
            {
                return false;
            }

            return DearmorAesGmacSiv(packet, key.Slice(0, SymmetricKeyLength));
        }

        if (cipher != CipherC25519Poly1305None && cipher != CipherC25519Poly1305Salsa2012)
        {
            return false;
        }

        Span<byte> mangledKey = stackalloc byte[MangledKeyLength];
        MangleKey(key.Slice(0, KeyLength), packet, mangledKey);

        var payloadLength = packet.Length - IndexVerb;
        var keystreamLength = DiscardedKeystreamBytes + (cipher == CipherC25519Poly1305Salsa2012 ? payloadLength : 0);
        var keystream = new byte[keystreamLength];
        ZeroTierSalsa20.GenerateKeyStream12(mangledKey, packet.Slice(0, 8), keystream);

        Span<byte> tag = stackalloc byte[16];
        ComputePoly1305Tag(packet.Slice(IndexVerb, payloadLength), keystream.AsSpan(0, MacKeyLength), tag);

        if (!CryptographicOperations.FixedTimeEquals(
                packet.Slice(IndexMac, MacLength),
                tag.Slice(0, MacLength)))
        {
            return false;
        }

        if (cipher == CipherC25519Poly1305Salsa2012)
        {
            XorInPlace(packet.Slice(IndexVerb, payloadLength), keystream.AsSpan(DiscardedKeystreamBytes));
        }

        return true;
    }

    private static void ArmorAesGmacSiv(Span<byte> packet, ReadOnlySpan<byte> key48)
    {
        if (key48.Length != SymmetricKeyLength)
        {
            throw new ArgumentException($"Key must be exactly {SymmetricKeyLength} bytes for AES-GMAC-SIV.", nameof(key48));
        }

        SetCipher(packet, CipherAesGmacSiv);

        Span<byte> k0 = stackalloc byte[KeyLength];
        Span<byte> k1 = stackalloc byte[KeyLength];
        DeriveAesGmacSivKeys(key48, k0, k1);

        var payloadLength = packet.Length - IndexVerb;

        Span<byte> nonce = stackalloc byte[AesGmacSivNonceLength];
        packet.Slice(0, 8).CopyTo(nonce);
        nonce.Slice(8, 4).Clear();

        var authData = new byte[AesGmacSivAadPaddedLength + payloadLength];
        packet.Slice(8, 10).CopyTo(authData.AsSpan(0, 10));
        authData[10] = (byte)(packet[IndexFlags] & 0xF8);
        packet.Slice(IndexVerb, payloadLength).CopyTo(authData.AsSpan(AesGmacSivAadPaddedLength));

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
        encryptedIvMac.Slice(8, 8).CopyTo(packet.Slice(IndexMac, 8));

        Span<byte> ctrIv = stackalloc byte[AesBlockLength];
        encryptedIvMac.CopyTo(ctrIv);
        ctrIv[12] &= 0x7F;
        AesCtrXorInPlace(aes, ctrIv, packet.Slice(IndexVerb, payloadLength));
    }

    private static bool DearmorAesGmacSiv(Span<byte> packet, ReadOnlySpan<byte> key48)
    {
        if (key48.Length != SymmetricKeyLength)
        {
            throw new ArgumentException($"Key must be exactly {SymmetricKeyLength} bytes for AES-GMAC-SIV.", nameof(key48));
        }

        Span<byte> k0 = stackalloc byte[KeyLength];
        Span<byte> k1 = stackalloc byte[KeyLength];
        DeriveAesGmacSivKeys(key48, k0, k1);

        Span<byte> encryptedIvMac = stackalloc byte[AesBlockLength];
        packet.Slice(0, 8).CopyTo(encryptedIvMac);
        packet.Slice(IndexMac, 8).CopyTo(encryptedIvMac.Slice(8, 8));

        using var aes = Aes.Create();
        aes.Key = k1.ToArray();

        Span<byte> ivMac = stackalloc byte[AesBlockLength];
        aes.DecryptEcb(encryptedIvMac, ivMac, PaddingMode.None);

        Span<byte> ctrIv = stackalloc byte[AesBlockLength];
        encryptedIvMac.CopyTo(ctrIv);
        ctrIv[12] &= 0x7F;

        var payloadLength = packet.Length - IndexVerb;
        AesCtrXorInPlace(aes, ctrIv, packet.Slice(IndexVerb, payloadLength));

        Span<byte> nonce = stackalloc byte[AesGmacSivNonceLength];
        ivMac.Slice(0, 8).CopyTo(nonce);
        nonce.Slice(8, 4).Clear();

        var authData = new byte[AesGmacSivAadPaddedLength + payloadLength];
        packet.Slice(8, 10).CopyTo(authData.AsSpan(0, 10));
        authData[10] = (byte)(packet[IndexFlags] & 0xF8);
        packet.Slice(IndexVerb, payloadLength).CopyTo(authData.AsSpan(AesGmacSivAadPaddedLength));

        Span<byte> gmacTag = stackalloc byte[AesGmacSivTagLength];
        ComputeGmacTag(k0, nonce, authData, gmacTag);

        Span<byte> reducedTag = stackalloc byte[8];
        for (var i = 0; i < reducedTag.Length; i++)
        {
            reducedTag[i] = (byte)(gmacTag[i] ^ gmacTag[i + 8]);
        }

        return CryptographicOperations.FixedTimeEquals(
            reducedTag,
            ivMac.Slice(8, 8));
    }

    private static void ComputeGmacTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (key.Length != KeyLength)
        {
            throw new ArgumentException($"Key must be {KeyLength} bytes.", nameof(key));
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
        if (key48.Length != SymmetricKeyLength)
        {
            throw new ArgumentException($"Key must be exactly {SymmetricKeyLength} bytes.", nameof(key48));
        }

        if (k0.Length != KeyLength)
        {
            throw new ArgumentException($"k0 must be {KeyLength} bytes.", nameof(k0));
        }

        if (k1.Length != KeyLength)
        {
            throw new ArgumentException($"k1 must be {KeyLength} bytes.", nameof(k1));
        }

        Span<byte> derived = stackalloc byte[SymmetricKeyLength];

        KbkdfHmacSha384(key48, label: KbkdfLabelAesGmacSivK0, context: 0, iter: 0, destination: derived);
        derived.Slice(0, KeyLength).CopyTo(k0);

        KbkdfHmacSha384(key48, label: KbkdfLabelAesGmacSivK1, context: 0, iter: 0, destination: derived);
        derived.Slice(0, KeyLength).CopyTo(k1);
    }

    private static void KbkdfHmacSha384(
        ReadOnlySpan<byte> key48,
        byte label,
        byte context,
        uint iter,
        Span<byte> destination)
    {
        if (key48.Length != SymmetricKeyLength)
        {
            throw new ArgumentException($"Key must be exactly {SymmetricKeyLength} bytes.", nameof(key48));
        }

        if (destination.Length < SymmetricKeyLength)
        {
            throw new ArgumentException($"Destination must be at least {SymmetricKeyLength} bytes.", nameof(destination));
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

        if (!HMACSHA384.TryHashData(key48, msg, destination.Slice(0, SymmetricKeyLength), out var written) || written != SymmetricKeyLength)
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

    private static void SetCipher(Span<byte> packet, byte cipher)
    {
        var flags = packet[IndexFlags];
        flags = (byte)((flags & 0xC7) | ((cipher << 3) & 0x38));
        if (cipher == CipherC25519Poly1305Salsa2012)
        {
            flags |= ZeroTierPacketHeader.FlagEncryptedDeprecated;
        }
        else
        {
            flags &= unchecked((byte)~ZeroTierPacketHeader.FlagEncryptedDeprecated);
        }

        packet[IndexFlags] = flags;
    }

    private static void MangleKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> packet, Span<byte> mangledKey)
    {
        if (key.Length < KeyLength)
        {
            throw new ArgumentException($"Key must be at least {KeyLength} bytes.", nameof(key));
        }

        if (packet.Length < ZeroTierPacketHeader.Length)
        {
            throw new ArgumentException("Packet is too short.", nameof(packet));
        }

        if (mangledKey.Length < MangledKeyLength)
        {
            throw new ArgumentException($"Mangled key must be {MangledKeyLength} bytes.", nameof(mangledKey));
        }

        for (var i = 0; i < 18; i++)
        {
            mangledKey[i] = (byte)(key[i] ^ packet[i]);
        }

        mangledKey[18] = (byte)(key[18] ^ (packet[IndexFlags] & 0xF8));
        mangledKey[19] = (byte)(key[19] ^ (packet.Length & 0xFF));
        mangledKey[20] = (byte)(key[20] ^ ((packet.Length >> 8) & 0xFF));

        for (var i = 21; i < MangledKeyLength; i++)
        {
            mangledKey[i] = key[i];
        }
    }

    private static void ComputePoly1305Tag(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, Span<byte> destination)
    {
        if (key.Length != MacKeyLength)
        {
            throw new ArgumentException($"Poly1305 key must be {MacKeyLength} bytes.", nameof(key));
        }

        if (destination.Length < 16)
        {
            throw new ArgumentException("Destination must be at least 16 bytes.", nameof(destination));
        }

        var mac = new Poly1305();
        mac.Init(new KeyParameter(key.ToArray()));
        mac.BlockUpdate(data.ToArray(), 0, data.Length);
        var tag = new byte[16];
        mac.DoFinal(tag, 0);
        tag.CopyTo(destination);
    }

    private static void XorInPlace(Span<byte> data, ReadOnlySpan<byte> keystream)
    {
        if (keystream.Length < data.Length)
        {
            throw new ArgumentException("Keystream is too short.", nameof(keystream));
        }

        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= keystream[i];
        }
    }
}
