using ZTSharp.Transport;

namespace ZTSharp.Tests;

public sealed class NodeFrameCodecTests
{
    [Fact]
    public void TryEncodeAndDecode_RoundtripsPayload()
    {
        const ulong networkId = 0x1122334455667788;
        const ulong sourceNodeId = 0xAABBCCDDEEFF0011;
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

        var frame = new byte[NodeFrameCodec.GetEncodedLength(payload.Length)];

        Assert.True(NodeFrameCodec.TryEncode(networkId, sourceNodeId, payload, frame, out var bytesWritten));
        Assert.Equal(frame.Length, bytesWritten);

        Assert.True(NodeFrameCodec.TryDecode(frame.AsSpan(0, bytesWritten), out var decodedNetworkId, out var decodedSourceNodeId, out var decodedPayload));
        Assert.Equal(networkId, decodedNetworkId);
        Assert.Equal(sourceNodeId, decodedSourceNodeId);
        Assert.True(payload.AsSpan().SequenceEqual(decodedPayload));

        Assert.True(NodeFrameCodec.TryDecode(frame.AsMemory(0, bytesWritten), out decodedNetworkId, out decodedSourceNodeId, out var decodedPayloadMemory));
        Assert.Equal(networkId, decodedNetworkId);
        Assert.Equal(sourceNodeId, decodedSourceNodeId);
        Assert.True(payload.AsSpan().SequenceEqual(decodedPayloadMemory.Span));
    }

    [Fact]
    public void TryEncode_Fails_WhenDestinationTooSmall()
    {
        var payload = new byte[] { 1, 2, 3 };
        Span<byte> destination = stackalloc byte[NodeFrameCodec.HeaderLength + payload.Length - 1];

        Assert.False(NodeFrameCodec.TryEncode(1UL, 2UL, payload, destination, out var bytesWritten));
        Assert.Equal(0, bytesWritten);
    }

    [Fact]
    public void TryDecode_Fails_WhenFrameTooShort()
    {
        Span<byte> frame = stackalloc byte[NodeFrameCodec.HeaderLength - 1];
        frame.Fill(0);

        Assert.False(NodeFrameCodec.TryDecode(frame, out var networkId, out var sourceNodeId, out var payload));
        Assert.Equal(0UL, networkId);
        Assert.Equal(0UL, sourceNodeId);
        Assert.True(payload.IsEmpty);
    }

    [Fact]
    public void TryDecode_Fails_WhenVersionMismatch()
    {
        var frame = new byte[NodeFrameCodec.HeaderLength];
        frame[0] = 2;

        Assert.False(NodeFrameCodec.TryDecode(frame, out var networkId, out var sourceNodeId, out var payload));
        Assert.Equal(0UL, networkId);
        Assert.Equal(0UL, sourceNodeId);
        Assert.True(payload.IsEmpty);
    }
}
