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
    private const int MaxEndpoints = 8;
    private const long HolePunchMinIntervalMs = 5_000;
    private const long HolePunchCacheTtlMs = 60_000;
    private const int HolePunchCacheMaxEntries = 2048;
    private const long PushDirectPathsCutoffTimeMs = 30_000;
    private const int PushDirectPathsCutoffLimit = 8;

    private const byte PushDirectPathsFlagForgetPath = 0x01;
    private const byte PushDirectPathsFlagClusterRedirect = 0x02;

    private readonly IZeroTierUdpTransport _udp;
    private readonly IPEndPoint _relayEndpoint;
    private readonly NodeId _remoteNodeId;
    private readonly object _lock = new();

    private IPEndPoint[] _directEndpoints = Array.Empty<IPEndPoint>();
    private readonly ConcurrentDictionary<string, long> _holePunchLastSentMs = new(StringComparer.Ordinal);
    private long _lastHolePunchCleanupMs;
    private long _lastDirectPathPushReceiveMs;
    private int _directPathPushCutoffCount;

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
            var endpoints = ZeroTierDirectEndpointSelection.Normalize([rendezvous.Endpoint], _relayEndpoint, maxEndpoints: MaxEndpoints);
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine($"[zerotier] RX RENDEZVOUS: {rendezvous.With} endpoints: {ZeroTierDirectEndpointSelection.Format(endpoints)} via {receivedVia}.");
            }

            lock (_lock)
            {
                _directEndpoints = endpoints;
            }

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

        var now = Environment.TickCount64;
        if (!RateGatePushDirectPaths(now))
        {
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine($"[zerotier] Drop: PUSH_DIRECT_PATHS rate-gated (peer={_remoteNodeId}).");
            }

            return ValueTask.CompletedTask;
        }

        var forget = new HashSet<string>(StringComparer.Ordinal);
        var redirect = new List<IPEndPoint>();
        var add = new List<IPEndPoint>();

        for (var i = 0; i < paths.Length; i++)
        {
            var flags = paths[i].Flags;
            var endpoint = paths[i].Endpoint;
            var key = FormatEndpointKey(endpoint);

            if ((flags & PushDirectPathsFlagForgetPath) != 0)
            {
                forget.Add(key);
                continue;
            }

            if ((flags & PushDirectPathsFlagClusterRedirect) != 0)
            {
                redirect.Add(endpoint);
            }
            else
            {
                add.Add(endpoint);
            }
        }

        IPEndPoint[] endpoints;
        lock (_lock)
        {
            var merged = _directEndpoints
                .Where(ep => !forget.Contains(FormatEndpointKey(ep)))
                .Concat(redirect)
                .Concat(add);

            endpoints = ZeroTierDirectEndpointSelection.Normalize(merged, _relayEndpoint, maxEndpoints: MaxEndpoints);
            _directEndpoints = endpoints;
        }

        if (endpoints.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        if (ZeroTierTrace.Enabled)
        {
            ZeroTierTrace.WriteLine($"[zerotier] RX PUSH_DIRECT_PATHS: endpoints: {ZeroTierDirectEndpointSelection.Format(endpoints)} (candidates: {paths.Length}).");
        }

        foreach (var endpoint in endpoints)
        {
            TrySendHolePunch(endpoint);
        }

        return ValueTask.CompletedTask;
    }

    private bool RateGatePushDirectPaths(long nowMs)
    {
        lock (_lock)
        {
            if (unchecked(nowMs - _lastDirectPathPushReceiveMs) <= PushDirectPathsCutoffTimeMs)
            {
                _directPathPushCutoffCount++;
            }
            else
            {
                _directPathPushCutoffCount = 0;
            }

            _lastDirectPathPushReceiveMs = nowMs;
            return _directPathPushCutoffCount < PushDirectPathsCutoffLimit;
        }
    }

    private void TrySendHolePunch(IPEndPoint endpoint)
    {
        var localSockets = _udp.LocalSockets;
        var now = Environment.TickCount64;
        CleanupHolePunchCacheIfNeeded(now);

        var junk = new byte[4];
        RandomNumberGenerator.Fill(junk);

        if (localSockets.Count == 0)
        {
            if (!ShouldSendHolePunch(localSocketId: 0, endpoint, now))
            {
                return;
            }

            TrySendHolePunchCore(localSocketId: 0, endpoint, junk);
            return;
        }

        for (var i = 0; i < localSockets.Count; i++)
        {
            var socketId = localSockets[i].Id;
            if (!ShouldSendHolePunch(socketId, endpoint, now))
            {
                continue;
            }

            TrySendHolePunchCore(socketId, endpoint, junk);
        }
    }

    private void TrySendHolePunchCore(int localSocketId, IPEndPoint endpoint, byte[] junk)
    {
        Task sendTask;
        try
        {
            ZeroTierTrace.WriteLine($"[zerotier] TX hole-punch to {endpoint} (socket={localSocketId}).");
            sendTask = _udp.SendAsync(localSocketId, endpoint, junk, CancellationToken.None);
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

    private bool ShouldSendHolePunch(int localSocketId, IPEndPoint endpoint, long nowMs)
    {
        var keyAddress = endpoint.Address;
        if (keyAddress.AddressFamily == AddressFamily.InterNetworkV6 && keyAddress.IsIPv4MappedToIPv6)
        {
            keyAddress = keyAddress.MapToIPv4();
        }

        var key = $"{localSocketId}|{keyAddress}:{endpoint.Port}";

        while (true)
        {
            if (_holePunchLastSentMs.TryGetValue(key, out var lastSent) &&
                unchecked(nowMs - lastSent) < HolePunchMinIntervalMs)
            {
                return false;
            }

            if (_holePunchLastSentMs.TryAdd(key, nowMs))
            {
                return true;
            }

            _holePunchLastSentMs.TryGetValue(key, out lastSent);
            if (unchecked(nowMs - lastSent) < HolePunchMinIntervalMs)
            {
                return false;
            }

            if (_holePunchLastSentMs.TryUpdate(key, nowMs, lastSent))
            {
                return true;
            }
        }
    }

    private void CleanupHolePunchCacheIfNeeded(long nowMs)
    {
        if (_holePunchLastSentMs.Count <= HolePunchCacheMaxEntries)
        {
            return;
        }

        var last = Volatile.Read(ref _lastHolePunchCleanupMs);
        if (last != 0 && unchecked(nowMs - last) < 10_000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastHolePunchCleanupMs, nowMs, last) != last)
        {
            return;
        }

        var cutoff = nowMs - HolePunchCacheTtlMs;
        foreach (var entry in _holePunchLastSentMs)
        {
            if (entry.Value <= cutoff)
            {
                _holePunchLastSentMs.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string FormatEndpointKey(IPEndPoint endpoint)
    {
        var address = endpoint.Address;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return $"{address}:{endpoint.Port}";
    }
}
