using System.Buffers.Binary;
using System.Net;

namespace ZTSharp.Tests;

public sealed class CodecValidationTests
{
    [Fact]
    public void NetworkAddressCodec_TryDecode_RejectsInvalidV4Prefix()
    {
        Span<byte> encoded = stackalloc byte[11];
        encoded[0] = 1; // version
        BinaryPrimitives.WriteInt32LittleEndian(encoded.Slice(1, 4), 1); // count
        encoded[5] = 4; // tag v4
        encoded[6] = 33; // invalid prefix
        encoded[7] = 1;
        encoded[8] = 2;
        encoded[9] = 3;
        encoded[10] = 4;

        Assert.False(NetworkAddressCodec.TryDecode(encoded, out _));
    }

    [Fact]
    public void NetworkAddressCodec_TryDecode_RejectsInvalidV6Prefix()
    {
        var encoded = new byte[31];
        encoded[0] = 1; // version
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(1, 4), 1); // count
        encoded[5] = 6; // tag v6
        encoded[6] = 129; // invalid prefix

        var address = IPAddress.Parse("fd00::1").GetAddressBytes();
        Assert.Equal(16, address.Length);
        address.CopyTo(encoded.AsSpan(7, 16));

        Assert.False(NetworkAddressCodec.TryDecode(encoded, out _));
    }

    [Fact]
    public void PeerEndpointCodec_TryDecode_RejectsPortZero()
    {
        Span<byte> encoded = stackalloc byte[8];
        encoded[0] = 1; // version
        encoded[1] = 4; // tag v4
        BinaryPrimitives.WriteUInt16BigEndian(encoded.Slice(2, 2), 0);
        encoded[4] = 1;
        encoded[5] = 2;
        encoded[6] = 3;
        encoded[7] = 4;

        Assert.False(PeerEndpointCodec.TryDecode(encoded, out _));
    }
}

