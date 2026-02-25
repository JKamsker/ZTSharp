using System.Threading.Channels;

namespace ZTSharp.Internal;

internal sealed class NodeEventStream
{
    private readonly Action<NodeEvent> _onEventRaised;
    private Channel<NodeEvent>? _channel;

    public NodeEventStream(Action<NodeEvent> onEventRaised)
    {
        _onEventRaised = onEventRaised ?? throw new ArgumentNullException(nameof(onEventRaised));
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
        _onEventRaised(e);
        _channel?.Writer.TryWrite(e);
    }

    public void Complete() => _channel?.Writer.TryComplete();
}

