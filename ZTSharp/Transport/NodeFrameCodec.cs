using System.Buffers.Binary;

namespace ZTSharp.Transport;

internal static class NodeFrameCodec
{
    private const byte FrameVersion = 1;
    public const int HeaderLength = 1 + sizeof(ulong) * 2;

    public static int GetEncodedLength(int payloadLength)
    {
        return HeaderLength + payloadLength;
    }

    public static bool TryEncode(
        ulong networkId,
        ulong sourceNodeId,
        ReadOnlySpan<byte> payload,
        Span<byte> destination,
        out int bytesWritten)
    {
        var requiredLength = GetEncodedLength(payload.Length);
        if (destination.Length < requiredLength)
        {
            bytesWritten = 0;
            return false;
        }

        var frameBytes = destination;
        frameBytes[0] = FrameVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(frameBytes.Slice(1, sizeof(ulong)), networkId);
        BinaryPrimitives.WriteUInt64LittleEndian(
            frameBytes.Slice(1 + sizeof(ulong), sizeof(ulong)),
            sourceNodeId);
        payload.CopyTo(frameBytes.Slice(HeaderLength, payload.Length));
        bytesWritten = requiredLength;
        return true;
    }

    public static ReadOnlyMemory<byte> Encode(
        ulong networkId,
        ulong sourceNodeId,
        ReadOnlySpan<byte> payload)
    {
        var encoded = new byte[GetEncodedLength(payload.Length)];
        if (!TryEncode(networkId, sourceNodeId, payload, encoded, out var bytesWritten))
        {
            throw new InvalidOperationException("Encoded frame did not fit destination buffer.");
        }

        return encoded.AsMemory(0, bytesWritten);
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> frame,
        out ulong networkId,
        out ulong sourceNodeId,
        out ReadOnlySpan<byte> payload)
    {
        if (frame.Length < HeaderLength || frame[0] != FrameVersion)
        {
            networkId = 0;
            sourceNodeId = 0;
            payload = ReadOnlySpan<byte>.Empty;
            return false;
        }

        networkId = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(1, sizeof(ulong)));
        sourceNodeId = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(1 + sizeof(ulong), sizeof(ulong)));
        payload = frame[HeaderLength..];
        return true;
    }

    public static bool TryDecode(
        ReadOnlyMemory<byte> frame,
        out ulong networkId,
        out ulong sourceNodeId,
        out ReadOnlyMemory<byte> payload)
    {
        if (!TryDecode(frame.Span, out networkId, out sourceNodeId, out var decodedPayload))
        {
            payload = ReadOnlyMemory<byte>.Empty;
            return false;
        }

        var payloadOffset = frame.Length - decodedPayload.Length;
        payload = frame.Slice(payloadOffset, decodedPayload.Length);
        return true;
    }
}
