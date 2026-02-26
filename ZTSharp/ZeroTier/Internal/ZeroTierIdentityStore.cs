using System.Buffers.Binary;
using ZTSharp.Internal;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierIdentityStore
{
    private static ReadOnlySpan<byte> Magic => "ZTID"u8;
    private const byte Version = 1;
    private const int HeaderLength = 4 + 1;
    private const int PayloadLength = 8 + ZeroTierIdentity.PublicKeyLength + ZeroTierIdentity.PrivateKeyLength;
    private const int FileLength = HeaderLength + PayloadLength;

    public static bool TryLoad(string path, out ZeroTierIdentity identity)
    {
        identity = default!;
        if (!File.Exists(path))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

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
        if (nodeIdValue == 0 || nodeIdValue > NodeId.MaxValue)
        {
            return false;
        }

        var publicKey = bytes.AsSpan(5 + 8, ZeroTierIdentity.PublicKeyLength).ToArray();
        var privateKey = bytes.AsSpan(5 + 8 + ZeroTierIdentity.PublicKeyLength, ZeroTierIdentity.PrivateKeyLength).ToArray();

        identity = new ZeroTierIdentity(new NodeId(nodeIdValue), publicKey, privateKey);
        return true;
    }

    public static void Save(string path, ZeroTierIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.PrivateKey is null)
        {
            throw new ArgumentException("Identity must include a private key.", nameof(identity));
        }

        var bytes = new byte[FileLength];
        Magic.CopyTo(bytes.AsSpan(0, 4));
        bytes[4] = Version;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(5, 8), identity.NodeId.Value);
        identity.PublicKey.CopyTo(bytes.AsSpan(5 + 8, ZeroTierIdentity.PublicKeyLength));
        identity.PrivateKey.CopyTo(bytes.AsSpan(5 + 8 + ZeroTierIdentity.PublicKeyLength, ZeroTierIdentity.PrivateKeyLength));

        AtomicFile.WriteAllBytes(path, bytes);
    }
}
