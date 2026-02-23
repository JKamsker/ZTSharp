namespace JKamsker.LibZt;

/// <summary>
/// Event published when a virtual frame is delivered between nodes in the managed in-memory transport.
/// </summary>
public sealed record class ZtNetworkFrame(
    ulong NetworkId,
    ulong SourceNodeId,
    byte[] Payload,
    DateTimeOffset TimestampUtc);
