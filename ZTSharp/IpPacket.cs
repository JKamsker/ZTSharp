namespace ZTSharp;

public readonly record struct IpPacket(
    ulong SourceNodeId,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset TimestampUtc);

