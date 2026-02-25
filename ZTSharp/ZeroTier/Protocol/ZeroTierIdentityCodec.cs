using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierIdentityCodec
{
    private const int AddressLength = 5;
    private const byte IdentityTypeC25519 = 0;

    public static int GetSerializedLength(ZeroTierIdentity identity, bool includePrivate)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var length = AddressLength + 1 + ZeroTierIdentity.PublicKeyLength + 1;
        if (includePrivate && identity.PrivateKey is not null)
        {
            length += ZeroTierIdentity.PrivateKeyLength;
        }

        return length;
    }

    public static byte[] Serialize(ZeroTierIdentity identity, bool includePrivate = false)
    {
        var bytes = new byte[GetSerializedLength(identity, includePrivate)];
        _ = Serialize(identity, bytes, includePrivate);
        return bytes;
    }

    public static int Serialize(ZeroTierIdentity identity, Span<byte> destination, bool includePrivate)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var requiredLength = GetSerializedLength(identity, includePrivate);
        if (destination.Length < requiredLength)
        {
            throw new ArgumentException($"Destination must be at least {requiredLength} bytes.", nameof(destination));
        }

        ZeroTierBinaryPrimitives.WriteUInt40BigEndian(destination.Slice(0, AddressLength), identity.NodeId.Value);
        destination[AddressLength] = IdentityTypeC25519;
        identity.PublicKey.CopyTo(destination.Slice(AddressLength + 1, ZeroTierIdentity.PublicKeyLength));

        var includePrivateKey = includePrivate && identity.PrivateKey is not null;
        destination[AddressLength + 1 + ZeroTierIdentity.PublicKeyLength] =
            includePrivateKey ? (byte)ZeroTierIdentity.PrivateKeyLength : (byte)0;

        if (includePrivateKey)
        {
            identity.PrivateKey!.CopyTo(destination.Slice(AddressLength + 1 + ZeroTierIdentity.PublicKeyLength + 1));
        }

        return requiredLength;
    }

    public static ZeroTierIdentity Deserialize(ReadOnlySpan<byte> data, out int bytesRead)
    {
        if (data.Length < AddressLength + 1 + ZeroTierIdentity.PublicKeyLength + 1)
        {
            throw new FormatException("Identity data is too short.");
        }

        var nodeId = ReadUInt40(data.Slice(0, AddressLength));
        var type = data[AddressLength];
        if (type != IdentityTypeC25519)
        {
            throw new FormatException($"Unsupported identity type: {type}.");
        }

        var publicKey = data.Slice(AddressLength + 1, ZeroTierIdentity.PublicKeyLength).ToArray();
        var privateKeyLen = data[AddressLength + 1 + ZeroTierIdentity.PublicKeyLength];

        byte[]? privateKey = null;
        var total = AddressLength + 1 + ZeroTierIdentity.PublicKeyLength + 1;
        if (privateKeyLen != 0)
        {
            if (privateKeyLen != ZeroTierIdentity.PrivateKeyLength)
            {
                throw new FormatException($"Invalid private key length: {privateKeyLen}.");
            }

            if (data.Length < total + ZeroTierIdentity.PrivateKeyLength)
            {
                throw new FormatException("Identity data is too short for private key.");
            }

            privateKey = data.Slice(total, ZeroTierIdentity.PrivateKeyLength).ToArray();
            total += ZeroTierIdentity.PrivateKeyLength;
        }

        bytesRead = total;
        return new ZeroTierIdentity(new NodeId(nodeId), publicKey, privateKey);
    }

    private static ulong ReadUInt40(ReadOnlySpan<byte> data)
    {
        if (data.Length < AddressLength)
        {
            throw new ArgumentException("Address must be at least 5 bytes.", nameof(data));
        }

        return
            ((ulong)data[0] << 32) |
            ((ulong)data[1] << 24) |
            ((ulong)data[2] << 16) |
            ((ulong)data[3] << 8) |
            data[4];
    }

}
