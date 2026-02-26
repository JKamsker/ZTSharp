using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierInlineCom
{
    public static byte[] GetInlineCom(byte[] networkConfigDictionaryBytes)
    {
        ArgumentNullException.ThrowIfNull(networkConfigDictionaryBytes);

        if (!ZeroTierDictionary.TryGet(networkConfigDictionaryBytes, "C", out var comBytes) || comBytes.Length == 0)
        {
            throw new InvalidOperationException("Network config does not contain a certificate of membership (key 'C').");
        }

        if (!ZeroTierCertificateOfMembershipCodec.TryGetSerializedLength(comBytes, out var comLen))
        {
            throw new InvalidOperationException("Network config contains an invalid certificate of membership (key 'C').");
        }

        if (comLen != comBytes.Length)
        {
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine(
                    $"[zerotier] COM length mismatch: dictionary value has {comBytes.Length} bytes, certificate is {comLen} bytes. Truncating inline COM.");
            }

            comBytes = comBytes.AsSpan(0, comLen).ToArray();
        }

        return comBytes;
    }
}

