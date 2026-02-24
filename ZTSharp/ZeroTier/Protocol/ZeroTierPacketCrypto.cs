using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierPacketCrypto
{
    private const int KeyLength = 32;
    private const int MangledKeyLength = 32;
    private const int MacKeyLength = 32;
    private const int DiscardedKeystreamBytes = 64;
    private const int MacLength = 8;

    private const int IndexFlags = 18;
    private const int IndexMac = 19;
    private const int IndexVerb = 27;

    private const byte CipherC25519Poly1305None = 0;
    private const byte CipherC25519Poly1305Salsa2012 = 1;

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

