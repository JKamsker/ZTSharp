namespace JKamsker.LibZt.Sockets;

/// <summary>
/// Represents a received managed UDP datagram.
/// </summary>
public sealed record class ZtUdpDatagram(
    ulong SourceNodeId,
    int SourcePort,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset TimestampUtc);
