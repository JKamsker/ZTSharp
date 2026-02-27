using System.Collections.Concurrent;
using System.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierPeerPathNegotiationManager
{
    private const long MinSendIntervalMs = 10_000;
    private const long NegotiationStateTtlMs = 300_000;
    private const int MaxNegotiationStates = 4096;

    private readonly Func<long> _nowMs;
    private readonly ConcurrentDictionary<ZeroTierPeerNegotiationPathKey, NegotiationState> _state = new();
    private long _lastCleanupMs;

    public ZeroTierPeerPathNegotiationManager(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public void HandleInboundRequest(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint, short remoteUtility)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        var now = _nowMs();
        CleanupIfNeeded(now);
        var key = new ZeroTierPeerNegotiationPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        _state.AddOrUpdate(
            key,
            _ => new NegotiationState(remoteUtility, LastReceivedMs: now, LastSentMs: 0),
            (_, existing) => existing with { RemoteUtility = remoteUtility, LastReceivedMs = now });
    }

    public bool TryGetRemoteUtility(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint, out short remoteUtility)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        var now = _nowMs();
        CleanupIfNeeded(now);
        var key = new ZeroTierPeerNegotiationPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        if (_state.TryGetValue(key, out var state))
        {
            if (state.LastReceivedMs != 0 && unchecked(now - state.LastReceivedMs) > NegotiationStateTtlMs)
            {
                _state.TryRemove(new KeyValuePair<ZeroTierPeerNegotiationPathKey, NegotiationState>(key, state));
                remoteUtility = 0;
                return false;
            }

            remoteUtility = state.RemoteUtility;
            return true;
        }

        remoteUtility = 0;
        return false;
    }

    public bool TryMarkSent(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        var now = _nowMs();
        CleanupIfNeeded(now);
        var key = new ZeroTierPeerNegotiationPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));

        while (true)
        {
            if (!_state.TryGetValue(key, out var existing))
            {
                if (_state.TryAdd(key, new NegotiationState(RemoteUtility: 0, LastReceivedMs: 0, LastSentMs: now)))
                {
                    return true;
                }

                continue;
            }

            if (unchecked(now - existing.LastSentMs) < MinSendIntervalMs)
            {
                return false;
            }

            if (_state.TryUpdate(key, existing with { LastSentMs = now }, existing))
            {
                return true;
            }
        }
    }

    private void CleanupIfNeeded(long now)
    {
        var last = Volatile.Read(ref _lastCleanupMs);
        if (last != 0 && unchecked(now - last) < 10_000 && _state.Count <= MaxNegotiationStates)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanupMs, now, last) != last)
        {
            return;
        }

        foreach (var pair in _state)
        {
            var state = pair.Value;
            var touched = Math.Max(state.LastReceivedMs, state.LastSentMs);
            if (touched != 0 && unchecked(now - touched) > NegotiationStateTtlMs)
            {
                _state.TryRemove(pair);
            }
        }
    }

    private readonly record struct NegotiationState(short RemoteUtility, long LastReceivedMs, long LastSentMs);
}

internal readonly record struct ZeroTierPeerNegotiationPathKey(NodeId PeerNodeId, ZeroTierPeerPhysicalPathKey Path);

