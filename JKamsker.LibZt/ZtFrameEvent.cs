namespace JKamsker.LibZt;

/// <summary>
/// Event published when a virtual frame is delivered between nodes in the managed in-memory transport.
/// </summary>
[global::System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1710:IdentifiersShouldHaveCorrectSuffix", Justification = "Public event payload type name is part of API contract.")]
public sealed class ZtNetworkFrame : EventArgs
{
    public ZtNetworkFrame(
        ulong networkId,
        ulong sourceNodeId,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset timestampUtc)
    {
        NetworkId = networkId;
        SourceNodeId = sourceNodeId;
        Payload = payload;
        TimestampUtc = timestampUtc;
    }

    public ulong NetworkId { get; }

    public ulong SourceNodeId { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public DateTimeOffset TimestampUtc { get; }
}
