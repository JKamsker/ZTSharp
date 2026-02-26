namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpConnectionSignals
{
    public TaskCompletionSource<bool>? ConnectTcs { get; set; }

    public bool Connected { get; set; }
}
