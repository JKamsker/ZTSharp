namespace JKamsker.LibZt.Sockets;

/// <summary>
/// Represents a received managed UDP datagram.
/// </summary>
public readonly record struct ZtUdpDatagram(
    ulong SourceNodeId,
    int SourcePort,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset TimestampUtc);
