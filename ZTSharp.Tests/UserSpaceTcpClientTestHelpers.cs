using System.Threading.Channels;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

internal static class UserSpaceTcpClientTestHelpers
{
    public static async Task<int> ReadExactAsync(UserSpaceTcpClient client, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var readTotal = 0;
        while (readTotal < length)
        {
            var read = await client.ReadAsync(buffer.AsMemory(readTotal, length - readTotal), cancellationToken).ConfigureAwait(false);
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

