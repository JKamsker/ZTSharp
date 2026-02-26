using System.IO;
using ZTSharp.Sockets;

namespace ZTSharp.Tests;

public sealed class OverlayTcpIncomingBufferTests
{
    [Fact]
    public async Task WhenBufferOverflows_ReadThrowsIOException()
    {
        var incoming = new OverlayTcpIncomingBuffer();
        var segment = new byte[1024];

        for (var i = 0; i < 1024; i++)
        {
            Assert.True(incoming.TryWrite(segment));
        }

        Assert.False(incoming.TryWrite(segment));

        await Assert.ThrowsAsync<IOException>(() => incoming.ReadAsync(new byte[1], CancellationToken.None).AsTask());
    }
}
