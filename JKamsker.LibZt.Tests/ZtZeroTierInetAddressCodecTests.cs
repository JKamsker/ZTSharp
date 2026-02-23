using System.Net;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.Tests;

public sealed class ZtZeroTierInetAddressCodecTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTripsIpv4()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 9993);
        var buffer = new byte[ZtZeroTierInetAddressCodec.GetSerializedLength(endpoint)];

        var written = ZtZeroTierInetAddressCodec.Serialize(endpoint, buffer);
        Assert.Equal(buffer.Length, written);

        Assert.True(ZtZeroTierInetAddressCodec.TryDeserialize(buffer, out var parsed, out var bytesRead));
        Assert.Equal(buffer.Length, bytesRead);
        Assert.NotNull(parsed);
        Assert.Equal(endpoint, parsed);
    }

    [Fact]
    public void Serialize_Deserialize_RoundTripsIpv6()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("fd00::1"), 12345);
        var buffer = new byte[ZtZeroTierInetAddressCodec.GetSerializedLength(endpoint)];

        var written = ZtZeroTierInetAddressCodec.Serialize(endpoint, buffer);
        Assert.Equal(buffer.Length, written);

        Assert.True(ZtZeroTierInetAddressCodec.TryDeserialize(buffer, out var parsed, out var bytesRead));
        Assert.Equal(buffer.Length, bytesRead);
        Assert.NotNull(parsed);
        Assert.Equal(endpoint, parsed);
    }

    [Fact]
    public void Deserialize_CanReadNullAddress()
    {
        Assert.True(ZtZeroTierInetAddressCodec.TryDeserialize([0], out var endpoint, out var bytesRead));
        Assert.Equal(1, bytesRead);
        Assert.Null(endpoint);
    }

    [Fact]
    public void Deserialize_CanSkipUnknownTypes()
    {
        // Type 0x03: length-prefixed "other" address.
        var data = new byte[] { 0x03, 0x00, 0x04, 1, 2, 3, 4 };
        Assert.True(ZtZeroTierInetAddressCodec.TryDeserialize(data, out var endpoint, out var bytesRead));
        Assert.Equal(data.Length, bytesRead);
        Assert.Null(endpoint);
    }
}

