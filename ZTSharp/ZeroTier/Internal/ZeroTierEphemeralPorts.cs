using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierEphemeralPorts
{
    public static ushort Generate()
    {
        Span<byte> buffer = stackalloc byte[2];
        RandomNumberGenerator.Fill(buffer);
        var port = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        return (ushort)(49152 + (port % (ushort)(65535 - 49152)));
    }
}

