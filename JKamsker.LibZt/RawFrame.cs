namespace JKamsker.LibZt;

internal readonly struct RawFrame
{
    public RawFrame(ulong networkId, ulong sourceNodeId, ReadOnlyMemory<byte> payload)
    {
        NetworkId = networkId;
        SourceNodeId = sourceNodeId;
        Payload = payload;
    }

    public ulong NetworkId { get; }

    public ulong SourceNodeId { get; }

    public ReadOnlyMemory<byte> Payload { get; }
}

internal delegate void RawFrameReceivedHandler(in RawFrame frame);
