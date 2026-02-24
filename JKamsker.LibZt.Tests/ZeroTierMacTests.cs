using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.Tests;

public sealed class ZeroTierMacTests
{
    [Fact]
    public void FromAddress_ToAddress_Roundtrips()
    {
        const ulong networkId = 0x9ad07d01093a69e3UL;
        var nodeId = new NodeId(0x17e81f3f59UL);

        var mac = ZeroTierMac.FromAddress(nodeId, networkId);

        Assert.True(mac.IsLocallyAdministered);
        Assert.False(mac.IsMulticast);
        Assert.Equal(nodeId, mac.ToAddress(networkId));
    }

    [Fact]
    public void Broadcast_IsBroadcast()
    {
        Assert.True(ZeroTierMac.Broadcast.IsBroadcast);
    }

    [Fact]
    public void FirstOctetForNetwork_Blacklists_0x52()
    {
        Assert.Equal(0x32, ZeroTierMac.FirstOctetForNetwork(0x50));
    }
}

