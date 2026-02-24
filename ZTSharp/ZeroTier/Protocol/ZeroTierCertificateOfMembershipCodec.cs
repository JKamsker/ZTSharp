using System.Buffers.Binary;

namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierCertificateOfMembershipCodec
{
    public static bool TryGetSerializedLength(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;

        if (data.Length < 1 + 2 + 5)
        {
            return false;
        }

        if (data[0] != 1)
        {
            return false;
        }

        var qualifierCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1, 2));
        var qualifierBytes = 24 * (int)qualifierCount;
        if (qualifierBytes < 0)
        {
            return false;
        }

        var signedByStart = 1 + 2 + qualifierBytes;
        var minimum = signedByStart + 5;
        if (data.Length < minimum)
        {
            return false;
        }

        var signedByNonZero = false;
        for (var i = 0; i < 5; i++)
        {
            if (data[signedByStart + i] != 0)
            {
                signedByNonZero = true;
                break;
            }
        }

        var total = minimum + (signedByNonZero ? 96 : 0);
        if (data.Length < total)
        {
            return false;
        }

        length = total;
        return true;
    }
}

