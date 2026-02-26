using System.IO;
using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class UserSpaceTcpRemoteWindowTests
{
    [Fact]
    public async Task WriteAsync_DoesNotHang_WhenRemoteWindowIsZero_AndCloseSignalArrivesFirst()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort localPort = 50000;
        const ushort remotePort = 80;

        var receiver = new UserSpaceTcpReceiver();
        receiver.Initialize(initialRecvNext: 1234);

        await using var sender = new UserSpaceTcpSender(link, localIp, remoteIp, localPort, remotePort, mss: 1200, receiver);
        sender.InitializeSendState(iss: 1000);
        sender.UpdateRemoteSendWindow(windowSize: 0);

        sender.FailPendingOperations(new IOException("Simulated close."));

        var payload = new byte[1] { 1 };
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await sender
                .WriteAsync(payload, getAckNumber: () => receiver.RecvNext, CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
        });
    }
}
