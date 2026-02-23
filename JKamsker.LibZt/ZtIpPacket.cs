namespace JKamsker.LibZt;

public readonly record struct ZtIpPacket(
    ulong SourceNodeId,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset TimestampUtc);

