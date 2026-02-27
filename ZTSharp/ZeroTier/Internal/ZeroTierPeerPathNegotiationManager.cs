using System.Collections.Concurrent;
using System.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierPeerPathNegotiationManager
{
    private const long MinSendIntervalMs = 10_000;

    private readonly Func<long> _nowMs;
    private readonly ConcurrentDictionary<ZeroTierPeerNegotiationPathKey, NegotiationState> _state = new();

    public ZeroTierPeerPathNegotiationManager(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public void HandleInboundRequest(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint, short remoteUtility)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        var now = _nowMs();
        var key = new ZeroTierPeerNegotiationPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        _state.AddOrUpdate(
            key,
            _ => new NegotiationState(remoteUtility, LastReceivedMs: now, LastSentMs: 0),
            (_, existing) => existing with { RemoteUtility = remoteUtility, LastReceivedMs = now });
    }

    public bool TryGetRemoteUtility(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint, out short remoteUtility)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        var key = new ZeroTierPeerNegotiationPathKey(peerNodeId, new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint));
        if (_state.TryGetValue(key, out var state))
        {
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

    private readonly record struct NegotiationState(short RemoteUtility, long LastReceivedMs, long LastSentMs);
}

internal readonly record struct ZeroTierPeerNegotiationPathKey(NodeId PeerNodeId, ZeroTierPeerPhysicalPathKey Path);

