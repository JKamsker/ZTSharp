using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierErrorFormatting
{
    internal static string FormatError(ZeroTierVerb inReVerb, byte errorCode, ulong? networkId)
    {
        var message = errorCode switch
        {
            0x01 => "Invalid request.",
            0x02 => "Bad/unsupported protocol version.",
            0x03 => "Object not found.",
            0x04 => "Identity collision.",
            0x05 => "Unsupported operation.",
            0x06 => "Network membership certificate required (COM update needed).",
            0x07 => "Network access denied (not authorized).",
            0x08 => "Unwanted multicast.",
            0x09 => "Network authentication required (external/2FA).",
            _ => $"Unknown error (0x{errorCode:x2})."
        };

        var prefix = inReVerb switch
        {
            ZeroTierVerb.MulticastGather => "ERROR(MULTICAST_GATHER)",
            ZeroTierVerb.Whois => "ERROR(WHOIS)",
            ZeroTierVerb.ExtFrame => "ERROR(EXT_FRAME)",
            ZeroTierVerb.Frame => "ERROR(FRAME)",
            _ => $"ERROR({inReVerb})"
        };

        return networkId is null
            ? $"{prefix}: {message}"
            : $"{prefix}: {message} (network: 0x{networkId:x16})";
    }
}
