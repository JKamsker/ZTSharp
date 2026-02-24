using System.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierMulticastGroupTests
{
    [Fact]
    public void DeriveForAddressResolution_Ipv4_UsesBroadcastMacAndAdi()
    {
        var group = ZeroTierMulticastGroup.DeriveForAddressResolution(IPAddress.Parse("10.121.15.99"));

        Assert.Equal(ZeroTierMac.Broadcast, group.Mac);
        Assert.Equal(0x0a790f63u, group.Adi);
    }

    [Fact]
    public void DeriveForAddressResolution_Ipv6_UsesSolicitedNodeMulticastMac()
    {
        var group = ZeroTierMulticastGroup.DeriveForAddressResolution(IPAddress.Parse("fd00::1"));

        Assert.Equal(new ZeroTierMac(0x3333ff000001UL), group.Mac);
        Assert.Equal(0u, group.Adi);
    }
}

