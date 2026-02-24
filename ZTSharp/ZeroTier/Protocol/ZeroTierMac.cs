namespace ZTSharp.ZeroTier.Protocol;

/// <summary>
/// Represents a 48-bit Ethernet MAC address used by ZeroTier.
/// </summary>
internal readonly record struct ZeroTierMac(ulong Value)
{
    public const ulong MaxValue = 0xFFFFFFFFFFFFUL;

    public static readonly ZeroTierMac Broadcast = new(MaxValue);

    public bool IsBroadcast => (Value & MaxValue) == MaxValue;

    public bool IsMulticast => (Value & 0x0100_0000_0000UL) != 0;

    public bool IsLocallyAdministered => (Value & 0x0200_0000_0000UL) != 0;

    public static ZeroTierMac FromAddress(NodeId nodeId, ulong networkId)
    {
        if (nodeId.Value > NodeId.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, "Node id must fit in 40 bits.");
        }

        var m = ((ulong)FirstOctetForNetwork(networkId)) << 40;
        m |= nodeId.Value;
        m ^= ((networkId >> 8) & 0xFFUL) << 32;
        m ^= ((networkId >> 16) & 0xFFUL) << 24;
        m ^= ((networkId >> 24) & 0xFFUL) << 16;
        m ^= ((networkId >> 32) & 0xFFUL) << 8;
        m ^= (networkId >> 40) & 0xFFUL;
        return new ZeroTierMac(m & MaxValue);
    }

    public NodeId ToAddress(ulong networkId)
    {
        var a = Value & 0xFFFF_FFFFFFUL;
        a ^= ((networkId >> 8) & 0xFFUL) << 32;
        a ^= ((networkId >> 16) & 0xFFUL) << 24;
        a ^= ((networkId >> 24) & 0xFFUL) << 16;
        a ^= ((networkId >> 32) & 0xFFUL) << 8;
        a ^= (networkId >> 40) & 0xFFUL;
        return new NodeId(a & NodeId.MaxValue);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 6)
        {
            throw new ArgumentException("Destination must be at least 6 bytes.", nameof(destination));
        }

        destination[0] = (byte)((Value >> 40) & 0xFF);
        destination[1] = (byte)((Value >> 32) & 0xFF);
        destination[2] = (byte)((Value >> 24) & 0xFF);
        destination[3] = (byte)((Value >> 16) & 0xFF);
        destination[4] = (byte)((Value >> 8) & 0xFF);
        destination[5] = (byte)(Value & 0xFF);
    }

    public static ZeroTierMac Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < 6)
        {
            throw new ArgumentException("Source must be at least 6 bytes.", nameof(source));
        }

        var value =
            ((ulong)source[0] << 40) |
            ((ulong)source[1] << 32) |
            ((ulong)source[2] << 24) |
            ((ulong)source[3] << 16) |
            ((ulong)source[4] << 8) |
            source[5];
        return new ZeroTierMac(value);
    }

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[6];
        CopyTo(bytes);
        return string.Create(17, bytes, static (span, b) =>
        {
            var i = 0;
            for (var j = 0; j < 6; j++)
            {
                if (j != 0)
                {
                    span[i++] = ':';
                }

                span[i++] = GetHexNibble(b[j] >> 4);
                span[i++] = GetHexNibble(b[j] & 0x0F);
            }
        });
    }

    public static byte FirstOctetForNetwork(ulong networkId)
    {
        var a = (byte)(((byte)(networkId & 0xFE)) | 0x02);
        return a == 0x52 ? (byte)0x32 : a;
    }

    private static char GetHexNibble(int value)
        => (char)(value < 10 ? ('0' + value) : ('a' + (value - 10)));
}
