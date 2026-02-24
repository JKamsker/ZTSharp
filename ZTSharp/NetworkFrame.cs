namespace ZTSharp;

/// <summary>
/// Event published when a frame is delivered between nodes via the selected transport.
/// </summary>
[global::System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1710:IdentifiersShouldHaveCorrectSuffix", Justification = "Public event payload type name is part of API contract.")]
public sealed class NetworkFrame : EventArgs
{
    public NetworkFrame(
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
