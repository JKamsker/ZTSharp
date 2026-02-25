using System.Globalization;
using System.Text;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierNetworkConfigRequestMetadata
{
    public static byte[] BuildDictionaryBytes()
    {
        var sb = new StringBuilder();

        AppendHexKeyValue(sb, "v", 7); // ZT_NETWORKCONFIG_VERSION
        AppendHexKeyValue(sb, "vend", 1); // ZT_VENDOR_ZEROTIER
        AppendHexKeyValue(sb, "pv", ZeroTierHelloClient.AdvertisedProtocolVersion); // Protocol (matches our HELLO advert)
        AppendHexKeyValue(sb, "majv", 1); // ZEROTIER_ONE_VERSION_MAJOR
        AppendHexKeyValue(sb, "minv", 12); // ZEROTIER_ONE_VERSION_MINOR
        AppendHexKeyValue(sb, "revv", 0); // ZEROTIER_ONE_VERSION_REVISION
        AppendHexKeyValue(sb, "mr", 1024); // ZT_MAX_NETWORK_RULES
        AppendHexKeyValue(sb, "mc", 128); // ZT_MAX_NETWORK_CAPABILITIES
        AppendHexKeyValue(sb, "mcr", 64); // ZT_MAX_CAPABILITY_RULES
        AppendHexKeyValue(sb, "mt", 128); // ZT_MAX_NETWORK_TAGS
        AppendHexKeyValue(sb, "f", 0); // Flags
        AppendHexKeyValue(sb, "revr", 1); // ZT_RULES_ENGINE_REVISION

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void AppendHexKeyValue(StringBuilder sb, string key, ulong value)
    {
        if (sb.Length != 0)
        {
            sb.Append('\n');
        }

        sb.Append(key);
        sb.Append('=');
        sb.Append(value.ToString("x16", CultureInfo.InvariantCulture));
    }
}

