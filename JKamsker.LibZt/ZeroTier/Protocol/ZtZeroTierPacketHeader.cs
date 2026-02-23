namespace JKamsker.LibZt.ZeroTier.Protocol;

internal readonly record struct ZtZeroTierPacketHeader(
    ulong PacketId,
    ZtNodeId Destination,
    ZtNodeId Source,
    byte Flags,
    ulong Mac,
    byte VerbRaw)
{
    public const int Length = 28;

    public const byte FlagEncryptedDeprecated = 0x80;
    public const byte FlagFragmented = 0x40;
    public const byte VerbFlagCompressed = 0x80;

    public bool IsFragmented => (Flags & FlagFragmented) != 0;

    public byte HopCount => (byte)(Flags & 0x07);

    public byte CipherSuite => (byte)((Flags >> 3) & 0x07);

    public ZtZeroTierVerb Verb => (ZtZeroTierVerb)(VerbRaw & 0x1F);

    public bool IsCompressed => (VerbRaw & VerbFlagCompressed) != 0;
}

