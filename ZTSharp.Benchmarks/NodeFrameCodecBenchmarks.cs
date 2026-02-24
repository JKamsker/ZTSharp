using BenchmarkDotNet.Attributes;
using ZTSharp.Transport;

namespace ZTSharp.Benchmarks;

[MemoryDiagnoser]
public class NodeFrameCodecBenchmarks
{
    private const ulong NetworkId = 0x9ad07d010980bd45UL;
    private const ulong SourceNodeId = 0x0123456789UL;

    [Params(0, 32, 128, 1024)]
    public int PayloadLength { get; set; }

    private byte[] _payload = null!;
    private byte[] _frame = null!;
    private int _frameLength;

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[PayloadLength];
        for (var i = 0; i < _payload.Length; i++)
        {
            _payload[i] = (byte)i;
        }

        _frame = new byte[NodeFrameCodec.GetEncodedLength(PayloadLength)];
        if (!NodeFrameCodec.TryEncode(NetworkId, SourceNodeId, _payload, _frame, out _frameLength))
        {
            throw new InvalidOperationException("Could not encode setup frame.");
        }
    }

    [Benchmark]
    public int Encode()
    {
        NodeFrameCodec.TryEncode(NetworkId, SourceNodeId, _payload, _frame, out var bytesWritten);
        return bytesWritten;
    }

    [Benchmark]
    public int Decode()
    {
        NodeFrameCodec.TryDecode(_frame.AsSpan(0, _frameLength), out _, out _, out var payload);
        return payload.Length;
    }
}

