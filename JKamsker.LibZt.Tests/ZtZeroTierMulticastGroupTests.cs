using System.Net;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.Tests;

public sealed class ZtZeroTierMulticastGroupTests
{
    [Fact]
    public void DeriveForAddressResolution_Ipv4_UsesBroadcastMacAndAdi()
    {
        var group = ZtZeroTierMulticastGroup.DeriveForAddressResolution(IPAddress.Parse("10.121.15.99"));

        Assert.Equal(ZtZeroTierMac.Broadcast, group.Mac);
        Assert.Equal(0x0a790f63u, group.Adi);
    }

    [Fact]
    public void DeriveForAddressResolution_Ipv6_UsesSolicitedNodeMulticastMac()
    {
        var group = ZtZeroTierMulticastGroup.DeriveForAddressResolution(IPAddress.Parse("fd00::1"));

        Assert.Equal(new ZtZeroTierMac(0x3333ff000001UL), group.Mac);
        Assert.Equal(0u, group.Adi);
    }
}

