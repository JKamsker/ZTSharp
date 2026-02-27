using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ZTSharp.Internal;

internal sealed class NodeEventStream
{
    private const int DispatchQueueCapacity = 1024;

    private readonly Action<NodeEvent> _onEventRaised;
    private readonly ILogger _logger;
    private readonly Channel<NodeEvent> _dispatchQueue;
    private readonly Task _dispatchLoop;
    private Channel<NodeEvent>? _channel;

    public NodeEventStream(Action<NodeEvent> onEventRaised, ILogger logger)
    {
        _onEventRaised = onEventRaised ?? throw new ArgumentNullException(nameof(onEventRaised));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatchQueue = Channel.CreateBounded<NodeEvent>(new BoundedChannelOptions(capacity: DispatchQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _dispatchLoop = Task.Run(DispatchAsync);
    }

    public IAsyncEnumerable<NodeEvent> GetEventStream(CancellationToken cancellationToken)
    {
        var channel = _channel;
        if (channel is null)
        {
            var created = Channel.CreateUnbounded<NodeEvent>();
            channel = Interlocked.CompareExchange(ref _channel, created, null) ?? created;
        }

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    public void Publish(
        EventCode code,
        DateTimeOffset timestampUtc,
        ulong? networkId = null,
        string? message = null,
        Exception? error = null)
    {
        var e = new NodeEvent(code, timestampUtc, networkId, message, error);
        _dispatchQueue.Writer.TryWrite(e);
        _channel?.Writer.TryWrite(e);
    }

    public void Complete()
    {
        _dispatchQueue.Writer.TryComplete();
        _channel?.Writer.TryComplete();
    }

    private async Task DispatchAsync()
    {
        await foreach (var e in _dispatchQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _onEventRaised(e);
            }
#pragma warning disable CA1031 // User callbacks must not fault node operations.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_logger.IsEnabled(LogLevel.Error))
                {
#pragma warning disable CA1848
                    _logger.LogError(ex, "Node event handler threw for event {EventCode}", e.Code);
#pragma warning restore CA1848
                }
            }
        }
    }
}
