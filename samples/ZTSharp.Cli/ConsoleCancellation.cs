namespace ZTSharp.Cli;

internal sealed class ConsoleCancellation : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly ConsoleCancelEventHandler _handler;
    private bool _disposed;

    private ConsoleCancellation(CancellationTokenSource cts)
    {
        _cts = cts ?? throw new ArgumentNullException(nameof(cts));
        _handler = (_, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
        };

        Console.CancelKeyPress += _handler;
    }

    public CancellationToken Token => _cts.Token;

    public static ConsoleCancellation Create() => new(new CancellationTokenSource());

    public static ConsoleCancellation Create(TimeSpan timeout) => new(new CancellationTokenSource(timeout));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Console.CancelKeyPress -= _handler;
        _cts.Dispose();
    }
}

