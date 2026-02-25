using System.Globalization;

namespace ZTSharp;

/// <summary>
/// Represents a 40-bit ZeroTier node identifier (10 hex digits).
/// </summary>
public readonly record struct NodeId(ulong Value)
{
    public const ulong MaxValue = 0xFFFFFFFFFFUL;

    public string ToHexString() => Value.ToString("x10", CultureInfo.InvariantCulture);

    public static bool TryParse(string? value, out NodeId nodeId)
    {
        nodeId = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.AsSpan().Trim();
        var hasHexPrefix = false;
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hasHexPrefix = true;
            trimmed = trimmed.Slice(2);
        }

        if (trimmed.Length == 0)
        {
            return false;
        }

        var treatAsHex = hasHexPrefix || trimmed.Length == 10 || ContainsHexLetters(trimmed);
        if (treatAsHex)
        {
            if (!IsHex(trimmed))
            {
                return false;
            }

            if (!ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ||
                parsed == 0 ||
                parsed > MaxValue)
            {
                return false;
            }

            nodeId = new NodeId(parsed);
            return true;
        }

        if (!ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDec) ||
            parsedDec == 0 ||
            parsedDec > MaxValue)
        {
            return false;
        }

        nodeId = new NodeId(parsedDec);
        return true;
    }

    public static NodeId FromHex(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(2);
        }

        var parsed = ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (parsed > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Node id must fit in 40 bits (<= 0x{MaxValue:x10}).");
        }

        return new NodeId(parsed);
    }

    public override string ToString() => $"0x{ToHexString()}";

    private static bool ContainsHexLetters(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= 'a' and <= 'f' or >= 'A' and <= 'F')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= '0' and <= '9')
            {
                continue;
            }

            if (c is >= 'a' and <= 'f')
            {
                continue;
            }

            if (c is >= 'A' and <= 'F')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
