using System.Buffers;
using System.Buffers.Binary;

namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierWorldSignature
{
    private const ulong Prefix = 0x7f7f7f7f7f7f7f7fUL;
    private const ulong Suffix = 0xf7f7f7f7f7f7f7f7UL;

    public static bool IsValidSignedUpdate(ZeroTierWorld current, ZeroTierWorld update)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(update);

        if (current.Id != update.Id || current.Type != update.Type)
        {
            return false;
        }

        if (update.Timestamp <= current.Timestamp)
        {
            return false;
        }

        return VerifySignature(current.UpdatesMustBeSignedBy, update);
    }

    public static bool VerifySignature(ReadOnlySpan<byte> signerPublicKey, ZeroTierWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var message = SerializeForSign(world);
        return ZeroTierC25519.VerifySignature(signerPublicKey, message, world.Signature);
    }

    public static byte[] SerializeForSign(ZeroTierWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var writer = new ArrayBufferWriter<byte>(ZeroTierProtocolLimits.MaxWorldBytes);

        WriteUInt64(writer, Prefix);
        writer.GetSpan(1)[0] = (byte)world.Type;
        writer.Advance(1);

        WriteUInt64(writer, world.Id);
        WriteUInt64(writer, world.Timestamp);

        writer.Write(world.UpdatesMustBeSignedBy);

        writer.GetSpan(1)[0] = checked((byte)world.Roots.Count);
        writer.Advance(1);

        foreach (var root in world.Roots)
        {
            var identityBytes = ZeroTierIdentityCodec.Serialize(root.Identity, includePrivate: false);
            writer.Write(identityBytes);

            writer.GetSpan(1)[0] = checked((byte)root.StableEndpoints.Count);
            writer.Advance(1);

            foreach (var endpoint in root.StableEndpoints)
            {
                var len = ZeroTierInetAddressCodec.GetSerializedLength(endpoint);
                var span = writer.GetSpan(len);
                _ = ZeroTierInetAddressCodec.Serialize(endpoint, span);
                writer.Advance(len);
            }
        }

        if (world.Type == ZeroTierWorldType.Moon)
        {
            WriteUInt16(writer, 0);
        }

        WriteUInt64(writer, Suffix);

        return writer.WrittenSpan.ToArray();
    }

    private static void WriteUInt16(ArrayBufferWriter<byte> writer, ushort value)
    {
        var span = writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(span, value);
        writer.Advance(2);
    }

    private static void WriteUInt64(ArrayBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(8);
        BinaryPrimitives.WriteUInt64BigEndian(span, value);
        writer.Advance(8);
    }
}
