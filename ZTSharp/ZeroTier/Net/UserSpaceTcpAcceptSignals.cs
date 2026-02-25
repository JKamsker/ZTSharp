namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpAcceptSignals
{
    public TaskCompletionSource<bool>? AcceptTcs { get; set; }

    public bool Connected { get; set; }
}
