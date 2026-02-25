namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierBinaryPrimitives
{
    public static ulong ReadUInt40BigEndian(ReadOnlySpan<byte> value)
    {
        if (value.Length < 5)
        {
            throw new ArgumentException("Value must be at least 5 bytes.", nameof(value));
        }

        return
            ((ulong)value[0] << 32) |
            ((ulong)value[1] << 24) |
            ((ulong)value[2] << 16) |
            ((ulong)value[3] << 8) |
            value[4];
    }

    public static void WriteUInt40BigEndian(Span<byte> destination, ulong value)
    {
        if (destination.Length < 5)
        {
            throw new ArgumentException("Destination must be at least 5 bytes.", nameof(destination));
        }

        destination[0] = (byte)((value >> 32) & 0xFF);
        destination[1] = (byte)((value >> 24) & 0xFF);
        destination[2] = (byte)((value >> 16) & 0xFF);
        destination[3] = (byte)((value >> 8) & 0xFF);
        destination[4] = (byte)(value & 0xFF);
    }
}
