namespace JKamsker.LibZt;

internal readonly struct ZtRawFrame
{
    public ZtRawFrame(ulong networkId, ulong sourceNodeId, ReadOnlyMemory<byte> payload)
    {
        NetworkId = networkId;
        SourceNodeId = sourceNodeId;
        Payload = payload;
    }

    public ulong NetworkId { get; }

    public ulong SourceNodeId { get; }

    public ReadOnlyMemory<byte> Payload { get; }
}

internal delegate void ZtRawFrameReceivedHandler(in ZtRawFrame frame);
