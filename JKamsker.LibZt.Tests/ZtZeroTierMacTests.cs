using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.Tests;

public sealed class ZtZeroTierMacTests
{
    [Fact]
    public void FromAddress_ToAddress_Roundtrips()
    {
        const ulong networkId = 0x9ad07d01093a69e3UL;
        var nodeId = new ZtNodeId(0x17e81f3f59UL);

        var mac = ZtZeroTierMac.FromAddress(nodeId, networkId);

        Assert.True(mac.IsLocallyAdministered);
        Assert.False(mac.IsMulticast);
        Assert.Equal(nodeId, mac.ToAddress(networkId));
    }

    [Fact]
    public void Broadcast_IsBroadcast()
    {
        Assert.True(ZtZeroTierMac.Broadcast.IsBroadcast);
    }

    [Fact]
    public void FirstOctetForNetwork_Blacklists_0x52()
    {
        Assert.Equal(0x32, ZtZeroTierMac.FirstOctetForNetwork(0x50));
    }
}

