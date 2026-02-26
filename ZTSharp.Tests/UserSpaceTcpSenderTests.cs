using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class UserSpaceTcpSenderTests
{
    [Fact]
    public async Task WriteAsync_AcceptsAckZero_AfterSequenceWrap()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        var receiver = new UserSpaceTcpReceiver();
        await using var sender = new UserSpaceTcpSender(
            link,
            localIp,
            remoteIp,
            localPort,
            remotePort,
            mss: 1200,
            receiver);

        sender.InitializeSendState(uint.MaxValue);

        var writeTask = sender.WriteAsync(new byte[] { 0x42 }, getAckNumber: () => 0, CancellationToken.None).AsTask();

        _ = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        sender.OnAckReceived(ack: 0);

        await writeTask.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

