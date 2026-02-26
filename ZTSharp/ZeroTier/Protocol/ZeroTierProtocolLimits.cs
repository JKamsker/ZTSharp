namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierProtocolLimits
{
    // ZT_PROTO_MAX_PACKET_LENGTH = ZT_MAX_PACKET_FRAGMENTS (7) * ZT_DEFAULT_PHYSMTU (1432) = 10024
    public const int MaxPacketBytes = 10024;
    public const int MaxWorldBytes = 16 * 1024;
    public const int MaxNetworkConfigBytes = 1 * 1024 * 1024;
    public const int MaxPushedDirectPaths = 32;
}

