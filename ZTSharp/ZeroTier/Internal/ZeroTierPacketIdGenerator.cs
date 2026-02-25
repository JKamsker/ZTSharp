using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierPacketIdGenerator
{
    public static ulong GeneratePacketId()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }
}

