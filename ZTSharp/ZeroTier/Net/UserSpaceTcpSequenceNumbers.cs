namespace ZTSharp.ZeroTier.Net;

internal static class UserSpaceTcpSequenceNumbers
{
    public static bool GreaterThan(uint a, uint b) => (int)(a - b) > 0;

    public static bool GreaterThanOrEqual(uint a, uint b) => (int)(a - b) >= 0;
}
