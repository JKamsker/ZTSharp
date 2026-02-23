using BenchmarkDotNet.Attributes;
using JKamsker.LibZt.Transport;

namespace JKamsker.LibZt.Benchmarks;

[MemoryDiagnoser]
public class InMemoryTransportBenchmarks
{
    private const ulong NetworkId = 0x9ad07d010980bd45UL;
    private const ulong Node1Id = 0x0000000001UL;
    private const ulong Node2Id = 0x0000000002UL;

    private InMemoryNodeTransport _transport = null!;
    private ReadOnlyMemory<byte> _payload;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _transport = new InMemoryNodeTransport();
        _payload = new byte[256];

        _ = await _transport.JoinNetworkAsync(NetworkId, Node1Id, OnFrameAsync).ConfigureAwait(false);
        _ = await _transport.JoinNetworkAsync(NetworkId, Node2Id, OnFrameAsync).ConfigureAwait(false);
    }

    [Benchmark]
    public Task Send_1to1()
        => _transport.SendFrameAsync(NetworkId, Node1Id, _payload);

    [GlobalCleanup]
    public void Cleanup()
        => _transport.Dispose();

    private static Task OnFrameAsync(
        ulong sourceNodeId,
        ulong networkId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

