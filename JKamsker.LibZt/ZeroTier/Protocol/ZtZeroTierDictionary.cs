using System.Text;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZtZeroTierDictionary
{
    public static bool TryGet(ReadOnlySpan<byte> dictionaryBytes, string key, out byte[] value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var keyBytes = Encoding.ASCII.GetBytes(key);
        var pos = 0;

        while (pos < dictionaryBytes.Length)
        {
            var lineStart = pos;
            while (pos < dictionaryBytes.Length && dictionaryBytes[pos] is not (byte)'\r' and not (byte)'\n')
            {
                pos++;
            }

            var line = dictionaryBytes.Slice(lineStart, pos - lineStart);

            while (pos < dictionaryBytes.Length && dictionaryBytes[pos] is (byte)'\r' or (byte)'\n')
            {
                pos++;
            }

            if (line.IsEmpty)
            {
                continue;
            }

            var eq = line.IndexOf((byte)'=');
            if (eq <= 0)
            {
                continue;
            }

            var lineKey = line.Slice(0, eq);
            if (!lineKey.SequenceEqual(keyBytes))
            {
                continue;
            }

            value = Unescape(line.Slice(eq + 1));
            return true;
        }

        value = Array.Empty<byte>();
        return false;
    }

    private static byte[] Unescape(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        var unescaped = new byte[value.Length];
        var w = 0;

        for (var i = 0; i < value.Length; i++)
        {
            var b = value[i];
            if (b == (byte)'\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                unescaped[w++] = next switch
                {
                    (byte)'r' => 13,
                    (byte)'n' => 10,
                    (byte)'0' => 0,
                    (byte)'e' => (byte)'=',
                    _ => next
                };
                continue;
            }

            unescaped[w++] = b;
        }

        return w == unescaped.Length ? unescaped : unescaped.AsSpan(0, w).ToArray();
    }
}

