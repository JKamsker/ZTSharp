using System.Net;
using ZTSharp.Transport;

namespace ZTSharp.Internal;

internal sealed class NodeTransportService
{
    private readonly NodeEventStream _events;
    private readonly Action<NetworkFrame> _onFrameReceived;
    private readonly RawFrameReceivedHandler _onRawFrameReceived;
    private readonly INodeTransport _transport;
    private readonly NodeOptions _options;

    public NodeTransportService(
        NodeEventStream events,
        Action<NetworkFrame> onFrameReceived,
        RawFrameReceivedHandler onRawFrameReceived,
        INodeTransport transport,
        NodeOptions options)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _onFrameReceived = onFrameReceived ?? throw new ArgumentNullException(nameof(onFrameReceived));
        _onRawFrameReceived = onRawFrameReceived ?? throw new ArgumentNullException(nameof(onRawFrameReceived));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IPEndPoint? GetLocalTransportEndpoint()
    {
        if (_transport is not OsUdpNodeTransport udpTransport)
        {
            return null;
        }

        var advertised = _options.AdvertisedTransportEndpoint;
        if (advertised is null)
        {
            return udpTransport.LocalEndpoint;
        }

        if (advertised.Port != 0)
        {
            return advertised;
        }

        return new IPEndPoint(advertised.Address, udpTransport.LocalEndpoint.Port);
    }

    public Task OnFrameReceivedAsync(
        ulong sourceNodeId,
        ulong networkId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawFrame = new RawFrame(networkId, sourceNodeId, payload);
        _onRawFrameReceived(in rawFrame);

        _onFrameReceived(
            new NetworkFrame(
                networkId,
                sourceNodeId,
                payload,
                DateTimeOffset.UtcNow));

        _events.Publish(EventCode.NetworkFrameReceived, DateTimeOffset.UtcNow, networkId, "Frame received");
        return Task.CompletedTask;
    }
}

