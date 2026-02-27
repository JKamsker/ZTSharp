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

    [Fact]
    public async Task FinGrace_AllowsLateDataFramesToBeRead()
    {
        var incoming = new OverlayTcpIncomingBuffer();
        incoming.MarkRemoteFinReceived();

        var payload = new byte[] { 1, 2, 3 };
        Assert.True(incoming.TryWrite(payload));

        var buffer = new byte[3];
        var read = await incoming.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal(3, read);
        Assert.Equal(payload, buffer);

        var eof = await incoming.ReadAsync(new byte[1], CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, eof);
    }

    [Fact]
    public async Task FinArrival_UnblocksReaderAwaitingReadAsync()
    {
        var incoming = new OverlayTcpIncomingBuffer();

        var readTask = incoming.ReadAsync(new byte[1], CancellationToken.None).AsTask();

        await Task.Yield();
        incoming.MarkRemoteFinReceived();

        var eof = await readTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, eof);
    }
}
