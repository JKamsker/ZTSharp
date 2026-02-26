using System.Net;
using Microsoft.Extensions.Logging;
using ZTSharp.Transport;

namespace ZTSharp.Internal;

internal sealed class NodeTransportService
{
    private readonly NodeEventStream _events;
    private readonly ILogger _logger;
    private readonly Action<NetworkFrame> _onFrameReceived;
    private readonly RawFrameReceivedHandler _onRawFrameReceived;
    private readonly INodeTransport _transport;
    private readonly NodeOptions _options;

    public NodeTransportService(
        NodeEventStream events,
        ILogger logger,
        Action<NetworkFrame> onFrameReceived,
        RawFrameReceivedHandler onRawFrameReceived,
        INodeTransport transport,
        NodeOptions options)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        try
        {
            _onRawFrameReceived(in rawFrame);
        }
#pragma warning disable CA1031 // User callbacks must not kill the receive loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
#pragma warning disable CA1848
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "RawFrameReceived handler faulted (networkId={NetworkId}, sourceNodeId={SourceNodeId}).", networkId, sourceNodeId);
            }
#pragma warning restore CA1848
        }

        try
        {
            _onFrameReceived(
                new NetworkFrame(
                    networkId,
                    sourceNodeId,
                    payload,
                    DateTimeOffset.UtcNow));
        }
#pragma warning disable CA1031 // User callbacks must not kill the receive loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
#pragma warning disable CA1848
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "FrameReceived handler faulted (networkId={NetworkId}, sourceNodeId={SourceNodeId}).", networkId, sourceNodeId);
            }
#pragma warning restore CA1848
        }

        try
        {
            _events.Publish(EventCode.NetworkFrameReceived, DateTimeOffset.UtcNow, networkId, "Frame received");
        }
#pragma warning disable CA1031 // User callbacks must not kill the receive loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
#pragma warning disable CA1848
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "EventRaised handler faulted while publishing NetworkFrameReceived (networkId={NetworkId}, sourceNodeId={SourceNodeId}).", networkId, sourceNodeId);
            }
#pragma warning restore CA1848
        }

        return Task.CompletedTask;
    }
}
