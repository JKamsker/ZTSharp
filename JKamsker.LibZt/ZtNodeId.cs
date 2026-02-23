using System.Globalization;

namespace JKamsker.LibZt;

/// <summary>
/// Represents a 64-bit ZeroTier node identifier.
/// </summary>
public readonly record struct ZtNodeId(ulong Value)
{
    public static ZtNodeId FromHex(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(2);
        }

        var parsed = ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new ZtNodeId(parsed);
    }

    public override string ToString() => $"0x{Value:x16}";
}
