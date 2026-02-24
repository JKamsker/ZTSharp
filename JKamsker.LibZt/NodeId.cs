using System.Globalization;

namespace JKamsker.LibZt;

/// <summary>
/// Represents a 40-bit ZeroTier node identifier (10 hex digits).
/// </summary>
public readonly record struct NodeId(ulong Value)
{
    public const ulong MaxValue = 0xFFFFFFFFFFUL;

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

    public override string ToString() => $"0x{Value:x10}";
}
