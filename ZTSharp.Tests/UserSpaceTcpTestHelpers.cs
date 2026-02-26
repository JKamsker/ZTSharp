using System.Threading.Channels;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

internal static class UserSpaceTcpTestHelpers
{
    public static Task<int> ReadExactAsync(UserSpaceTcpClient client, byte[] buffer, int length, CancellationToken cancellationToken)
        => ReadExactAsync(client.ReadAsync, buffer, length, cancellationToken);

    public static Task<int> ReadExactAsync(UserSpaceTcpServerConnection server, byte[] buffer, int length, CancellationToken cancellationToken)
        => ReadExactAsync(server.ReadAsync, buffer, length, cancellationToken);

    private static async Task<int> ReadExactAsync(Func<Memory<byte>, CancellationToken, ValueTask<int>> readAsync, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var readTotal = 0;
        while (readTotal < length)
        {
            var read = await readAsync(buffer.AsMemory(readTotal, length - readTotal), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return readTotal;
            }

            readTotal += read;
        }

        return readTotal;
    }
}

internal sealed class InspectableIpv4Link : IUserSpaceIpLink
{
    public Channel<ReadOnlyMemory<byte>> Incoming { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    public Channel<ReadOnlyMemory<byte>> Outgoing { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    public ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default)
    {
        Outgoing.Writer.TryWrite(ipPacket);
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
        => Incoming.Reader.ReadAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        Incoming.Writer.TryComplete();
        Outgoing.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
