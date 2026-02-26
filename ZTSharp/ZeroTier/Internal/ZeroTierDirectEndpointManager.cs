using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Linq;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDirectEndpointManager
{
    private readonly ZeroTierUdpTransport _udp;
    private readonly IPEndPoint _relayEndpoint;
    private readonly NodeId _remoteNodeId;

    private IPEndPoint[] _directEndpoints = Array.Empty<IPEndPoint>();

    public ZeroTierDirectEndpointManager(ZeroTierUdpTransport udp, IPEndPoint relayEndpoint, NodeId remoteNodeId)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(relayEndpoint);

        _udp = udp;
        _relayEndpoint = relayEndpoint;
        _remoteNodeId = remoteNodeId;
    }

    public IPEndPoint[] Endpoints => _directEndpoints;

    public async ValueTask HandleRendezvousFromRootAsync(ReadOnlyMemory<byte> payload, IPEndPoint receivedVia, CancellationToken cancellationToken)
    {
        if (ZeroTierRendezvousCodec.TryParse(payload.Span, out var rendezvous) && rendezvous.With == _remoteNodeId)
        {
            var endpoints = ZeroTierDirectEndpointSelection.Normalize([rendezvous.Endpoint], _relayEndpoint, maxEndpoints: 8);
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine($"[zerotier] RX RENDEZVOUS: {rendezvous.With} endpoints: {ZeroTierDirectEndpointSelection.Format(endpoints)} via {receivedVia}.");
            }

            _directEndpoints = endpoints;

            foreach (var endpoint in endpoints)
            {
                await SendHolePunchAsync(endpoint, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        ZeroTierTrace.WriteLine($"[zerotier] RX RENDEZVOUS (ignored) via {receivedVia}.");
    }

    public async ValueTask HandlePushDirectPathsFromRemoteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!ZeroTierPushDirectPathsCodec.TryParse(payload.Span, out var paths) || paths.Length == 0)
        {
            ZeroTierTrace.WriteLine("[zerotier] Drop: failed to parse PUSH_DIRECT_PATHS payload.");
            return;
        }

        var endpoints = ZeroTierDirectEndpointSelection.Normalize(paths.Select(p => p.Endpoint), _relayEndpoint, maxEndpoints: 8);
        if (endpoints.Length == 0)
        {
            return;
        }

        if (ZeroTierTrace.Enabled)
        {
            ZeroTierTrace.WriteLine($"[zerotier] RX PUSH_DIRECT_PATHS: endpoints: {ZeroTierDirectEndpointSelection.Format(endpoints)} (candidates: {paths.Length}).");
        }

        _directEndpoints = endpoints;

        foreach (var endpoint in endpoints)
        {
            await SendHolePunchAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask SendHolePunchAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var junk = new byte[4];
        RandomNumberGenerator.Fill(junk);

        try
        {
            ZeroTierTrace.WriteLine($"[zerotier] TX hole-punch to {endpoint}.");
            await _udp.SendAsync(endpoint, junk, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
        }
    }
}
