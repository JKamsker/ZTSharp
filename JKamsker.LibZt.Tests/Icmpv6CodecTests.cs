using System.Buffers.Binary;
using System.Net;
using JKamsker.LibZt.ZeroTier.Net;

namespace JKamsker.LibZt.Tests;

public sealed class Icmpv6CodecTests
{
    [Fact]
    public void ComputeChecksum_WhenApplied_ValidatesToZero()
    {
        var src = IPAddress.Parse("fd00::1");
        var dst = IPAddress.Parse("fd00::2");

        var message = new byte[8 + 2];
        message[0] = 128; // Echo Request
        message[1] = 0; // Code
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2, 2), 0); // checksum placeholder
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4, 2), 0x1234); // identifier
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6, 2), 1); // sequence
        message[8] = (byte)'h';
        message[9] = (byte)'i';

        var checksum = Icmpv6Codec.ComputeChecksum(src, dst, message);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2, 2), checksum);

        Assert.Equal((ushort)0, Icmpv6Codec.ComputeChecksum(src, dst, message));
    }
}

