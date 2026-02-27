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

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4 * 1024,
                options: FileOptions.SequentialScan);

            if (stream.Length != FileLength)
            {
                return false;
            }

            var bytes = new byte[FileLength];
            var totalRead = 0;
            while (totalRead < bytes.Length)
            {
                var read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                if (read == 0)
                {
                    return false;
                }

                totalRead += read;
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
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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

        AtomicFile.WriteSecretBytes(path, bytes);
    }
}
