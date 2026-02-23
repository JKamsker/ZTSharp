using System.Buffers.Binary;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal static class ZtZeroTierIdentityStore
{
    private static ReadOnlySpan<byte> Magic => "ZTID"u8;
    private const byte Version = 1;
    private const int HeaderLength = 4 + 1;
    private const int PayloadLength = 8 + ZtZeroTierIdentity.PublicKeyLength + ZtZeroTierIdentity.PrivateKeyLength;
    private const int FileLength = HeaderLength + PayloadLength;

    public static bool TryLoad(string path, out ZtZeroTierIdentity identity)
    {
        identity = default!;
        if (!File.Exists(path))
        {
            return false;
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != FileLength)
        {
            return false;
        }

        if (!bytes.AsSpan(0, 4).SequenceEqual(Magic))
        {
            return false;
        }

        if (bytes[4] != Version)
        {
            return false;
        }

        var nodeIdValue = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(5, 8));
        if (nodeIdValue == 0 || nodeIdValue > ZtNodeId.MaxValue)
        {
            return false;
        }

        var publicKey = bytes.AsSpan(5 + 8, ZtZeroTierIdentity.PublicKeyLength).ToArray();
        var privateKey = bytes.AsSpan(5 + 8 + ZtZeroTierIdentity.PublicKeyLength, ZtZeroTierIdentity.PrivateKeyLength).ToArray();

        identity = new ZtZeroTierIdentity(new ZtNodeId(nodeIdValue), publicKey, privateKey);
        return true;
    }

    public static void Save(string path, ZtZeroTierIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.PrivateKey is null)
        {
            throw new ArgumentException("Identity must include a private key.", nameof(identity));
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bytes = new byte[FileLength];
        Magic.CopyTo(bytes.AsSpan(0, 4));
        bytes[4] = Version;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(5, 8), identity.NodeId.Value);
        identity.PublicKey.CopyTo(bytes.AsSpan(5 + 8, ZtZeroTierIdentity.PublicKeyLength));
        identity.PrivateKey.CopyTo(bytes.AsSpan(5 + 8 + ZtZeroTierIdentity.PublicKeyLength, ZtZeroTierIdentity.PrivateKeyLength));

        File.WriteAllBytes(path, bytes);
    }
}

