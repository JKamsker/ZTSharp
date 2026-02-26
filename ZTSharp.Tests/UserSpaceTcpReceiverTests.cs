using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class UserSpaceTcpReceiverTests
{
    [Fact]
    public async Task ProcessSegmentAsync_TrimsOverlaps_And_ReleasesReceiveWindow()
    {
        var receiver = new UserSpaceTcpReceiver();
        receiver.Initialize(initialRecvNext: 1000);

        var segmentAt1100 = new byte[150_000];
        Array.Fill(segmentAt1100, (byte)1);

        var segmentAt1050 = new byte[80_000];
        Array.Fill(segmentAt1050, (byte)2);

        var inOrder = new byte[100];
        Array.Fill(inOrder, (byte)3);

        _ = await receiver.ProcessSegmentAsync(seq: 1100, tcpPayload: segmentAt1100, hasFin: false);
        _ = await receiver.ProcessSegmentAsync(seq: 1050, tcpPayload: segmentAt1050, hasFin: false);

        var reducedWindow = receiver.GetReceiveWindow();
        Assert.True(reducedWindow < ushort.MaxValue);

        _ = await receiver.ProcessSegmentAsync(seq: 1000, tcpPayload: inOrder, hasFin: false);

        var recoveredWindow = receiver.GetReceiveWindow();
        Assert.Equal(ushort.MaxValue, recoveredWindow);
    }
}
