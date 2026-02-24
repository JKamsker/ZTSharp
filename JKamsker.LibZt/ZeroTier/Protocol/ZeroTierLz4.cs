namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZeroTierLz4
{
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;

        var src = 0;
        var dst = 0;

        while (src < source.Length)
        {
            var token = source[src++];

            var literalLength = token >> 4;
            if (literalLength == 15)
            {
                while (true)
                {
                    if (src >= source.Length)
                    {
                        return false;
                    }

                    var b = source[src++];
                    literalLength += b;
                    if (b != 255)
                    {
                        break;
                    }
                }
            }

            if ((uint)(src + literalLength) > (uint)source.Length)
            {
                return false;
            }

            if ((uint)(dst + literalLength) > (uint)destination.Length)
            {
                return false;
            }

            source.Slice(src, literalLength).CopyTo(destination.Slice(dst));
            src += literalLength;
            dst += literalLength;

            if (src >= source.Length)
            {
                bytesWritten = dst;
                return true;
            }

            if (src + 2 > source.Length)
            {
                return false;
            }

            var offset = source[src] | (source[src + 1] << 8);
            src += 2;
            if (offset == 0 || offset > dst)
            {
                return false;
            }

            var matchLength = token & 0x0F;
            if (matchLength == 15)
            {
                while (true)
                {
                    if (src >= source.Length)
                    {
                        return false;
                    }

                    var b = source[src++];
                    matchLength += b;
                    if (b != 255)
                    {
                        break;
                    }
                }
            }

            matchLength += 4;
            if ((uint)(dst + matchLength) > (uint)destination.Length)
            {
                return false;
            }

            var matchStart = dst - offset;
            for (var i = 0; i < matchLength; i++)
            {
                destination[dst + i] = destination[matchStart + i];
            }

            dst += matchLength;
        }

        bytesWritten = dst;
        return true;
    }
}

