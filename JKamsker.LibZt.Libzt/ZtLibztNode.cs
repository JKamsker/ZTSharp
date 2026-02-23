using System.Net;
using JKamsker.LibZt;

namespace JKamsker.LibZt.Libzt;

/// <summary>
/// Wrapper around the upstream ZeroTier/libzt node implementation (via <c>ZeroTier.Sockets</c>).
/// </summary>
public sealed class ZtLibztNode : IAsyncDisposable
{
    private readonly ZtLibztNodeOptions _options;
    private readonly Action<ZeroTier.Core.Event>? _eventSink;
    private ZeroTier.Core.Node? _node;
    private int _started;
    private int _disposed;

    public ZtLibztNode(ZtLibztNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.StoragePath))
        {
            throw new ArgumentException("StoragePath is required.", nameof(options));
        }

        if (options.RandomPortRangeStart == 0 || options.RandomPortRangeEnd == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "RandomPortRangeStart/End must be in the range 1..65535.");
        }

        if (options.RandomPortRangeEnd < options.RandomPortRangeStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "RandomPortRangeEnd must be greater than or equal to RandomPortRangeStart.");
        }

        _options = options;
        _eventSink = options.EventSink;
    }

    public bool Online => _node?.Online ?? false;

    public ZtNodeId NodeId
    {
        get
        {
            var node = RequireStarted();
            var value = node.Id & ZtNodeId.MaxValue;
            if (value == 0)
            {
                throw new InvalidOperationException("libzt node id is not available yet.");
            }

            return new ZtNodeId(value);
        }
    }

    public ushort PrimaryPort => RequireStarted().PrimaryPort;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("libzt node is already started.");
        }

        Directory.CreateDirectory(_options.StoragePath);

        var node = new ZeroTier.Core.Node();

        static void ThrowIfError(int code, string operation)
        {
            if (code == 0)
            {
                return;
            }

            throw new InvalidOperationException($"{operation} failed (code {code}).");
        }

        ThrowIfError(node.InitFromStorage(_options.StoragePath), "InitFromStorage");
        ThrowIfError(node.InitAllowNetworkCaching(_options.AllowNetworkCaching), "InitAllowNetworkCaching");
        ThrowIfError(node.InitAllowPeerCaching(_options.AllowPeerCaching), "InitAllowPeerCaching");
        ThrowIfError(node.InitSetEventHandler(HandleEvent), "InitSetEventHandler");
        ThrowIfError(
            node.InitSetRandomPortRange(_options.RandomPortRangeStart, _options.RandomPortRangeEnd),
            "InitSetRandomPortRange");
        ThrowIfError(node.Start(), "Start");

        _node = node;

        await WaitUntilAsync(() => node.Online, TimeSpan.FromMilliseconds(50), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ThrowIfDisposed();
        var node = RequireStarted();
        var code = node.Join(networkId);
        if (code != 0)
        {
            throw new InvalidOperationException($"Join failed (code {code}).");
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<IPAddress> GetNetworkAddresses(ulong networkId)
    {
        ThrowIfDisposed();
        var node = RequireStarted();
        return node.GetNetworkAddresses(networkId);
    }

    public async Task WaitForNetworkTransportReadyAsync(
        ulong networkId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _ = RequireStarted();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await WaitUntilAsync(
                    () => _node!.IsNetworkTransportReady(networkId),
                    TimeSpan.FromMilliseconds(100),
                    cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Timed out waiting for the libzt network to become transport ready. " +
                "Is the member authorized in the controller?");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            _node?.Stop();
        }
        finally
        {
            _node = null;
        }

        return ValueTask.CompletedTask;
    }

    private void HandleEvent(ZeroTier.Core.Event nodeEvent)
    {
#pragma warning disable CA1031
        try
        {
            _eventSink?.Invoke(nodeEvent);
        }
        catch
        {
            // Suppress user handler exceptions - callbacks run on libzt threads.
        }
#pragma warning restore CA1031
    }

    private ZeroTier.Core.Node RequireStarted()
        => _node ?? throw new InvalidOperationException("libzt node is not started yet.");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        while (!condition())
        {
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
