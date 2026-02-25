namespace ZTSharp.Samples.ZeroTierSockets;

internal static class ConsoleCancellation
{
    public static CancellationTokenSource Setup()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }
}

