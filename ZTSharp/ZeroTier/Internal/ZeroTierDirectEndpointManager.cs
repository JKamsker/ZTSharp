using System.Net;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Linq;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDirectEndpointManager
{
    private const long HolePunchMinIntervalMs = 5_000;

    private readonly IZeroTierUdpTransport _udp;
    private readonly IPEndPoint _relayEndpoint;
    private readonly NodeId _remoteNodeId;

    private IPEndPoint[] _directEndpoints = Array.Empty<IPEndPoint>();
    private readonly ConcurrentDictionary<string, long> _holePunchLastSentMs = new(StringComparer.Ordinal);

    public ZeroTierDirectEndpointManager(IZeroTierUdpTransport udp, IPEndPoint relayEndpoint, NodeId remoteNodeId)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(relayEndpoint);

        _udp = udp;
        _relayEndpoint = relayEndpoint;
        _remoteNodeId = remoteNodeId;
    }

    public IPEndPoint[] Endpoints => _directEndpoints;

    public ValueTask HandleRendezvousFromRootAsync(ReadOnlyMemory<byte> payload, IPEndPoint receivedVia, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
                TrySendHolePunch(endpoint);
            }

            return ValueTask.CompletedTask;
        }

        ZeroTierTrace.WriteLine($"[zerotier] RX RENDEZVOUS (ignored) via {receivedVia}.");
        return ValueTask.CompletedTask;
    }

    public ValueTask HandlePushDirectPathsFromRemoteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ZeroTierPushDirectPathsCodec.TryParse(payload.Span, out var paths) || paths.Length == 0)
        {
            ZeroTierTrace.WriteLine("[zerotier] Drop: failed to parse PUSH_DIRECT_PATHS payload.");
            return ValueTask.CompletedTask;
        }

        var endpoints = ZeroTierDirectEndpointSelection.Normalize(paths.Select(p => p.Endpoint), _relayEndpoint, maxEndpoints: 8);
        if (endpoints.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        if (ZeroTierTrace.Enabled)
        {
            ZeroTierTrace.WriteLine($"[zerotier] RX PUSH_DIRECT_PATHS: endpoints: {ZeroTierDirectEndpointSelection.Format(endpoints)} (candidates: {paths.Length}).");
        }

        _directEndpoints = endpoints;

        foreach (var endpoint in endpoints)
        {
            TrySendHolePunch(endpoint);
        }

        return ValueTask.CompletedTask;
    }

    private void TrySendHolePunch(IPEndPoint endpoint)
    {
        if (!ShouldSendHolePunch(endpoint))
        {
            return;
        }

        var junk = new byte[4];
        RandomNumberGenerator.Fill(junk);

        Task sendTask;
        try
        {
            ZeroTierTrace.WriteLine($"[zerotier] TX hole-punch to {endpoint}.");
            sendTask = _udp.SendAsync(endpoint, junk, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or OperationCanceledException)
        {
            return;
        }

        _ = sendTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private bool ShouldSendHolePunch(IPEndPoint endpoint)
    {
        var now = Environment.TickCount64;
        var keyAddress = endpoint.Address;
        if (keyAddress.AddressFamily == AddressFamily.InterNetworkV6 && keyAddress.IsIPv4MappedToIPv6)
        {
            keyAddress = keyAddress.MapToIPv4();
        }

        var key = $"{keyAddress}:{endpoint.Port}";

        while (true)
        {
            if (_holePunchLastSentMs.TryGetValue(key, out var lastSent) &&
                unchecked(now - lastSent) < HolePunchMinIntervalMs)
            {
                return false;
            }

            if (_holePunchLastSentMs.TryAdd(key, now))
            {
                return true;
            }

            _holePunchLastSentMs.TryGetValue(key, out lastSent);
            if (unchecked(now - lastSent) < HolePunchMinIntervalMs)
            {
                return false;
            }

            if (_holePunchLastSentMs.TryUpdate(key, now, lastSent))
            {
                return true;
            }
        }
    }
}
