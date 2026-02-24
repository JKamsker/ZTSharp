namespace JKamsker.LibZt.ZeroTier.Internal;

internal static class ZtZeroTierTrace
{
    private static readonly Lazy<bool> LazyEnabled = new(() =>
        bool.TryParse(Environment.GetEnvironmentVariable("LIBZT_ZEROTIER_TRACE"), out var parsed) && parsed);

    public static bool Enabled => LazyEnabled.Value;

    public static void WriteLine(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine(message);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}

