using System.Collections.Concurrent;
using System.Net;
using System.Linq;

namespace ZTSharp.Transport.Internal;

internal sealed class OsUdpPeerRegistry
{
    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, IPEndPoint>> s_networkDirectory = new();

    private readonly bool _enablePeerDiscovery;
    private readonly Func<IPEndPoint, IPEndPoint> _normalizeEndpoint;

    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, IPEndPoint>> _networkPeers = new();
    private readonly ConcurrentDictionary<ulong, ulong> _localNodeIds = new();

    public OsUdpPeerRegistry(bool enablePeerDiscovery, Func<IPEndPoint, IPEndPoint> normalizeEndpoint)
    {
        ArgumentNullException.ThrowIfNull(normalizeEndpoint);

        _enablePeerDiscovery = enablePeerDiscovery;
        _normalizeEndpoint = normalizeEndpoint;
    }

    public bool TryGetLocalNodeId(ulong networkId, out ulong nodeId)
        => _localNodeIds.TryGetValue(networkId, out nodeId);

    public void SetLocalNodeId(ulong networkId, ulong nodeId)
        => _localNodeIds[networkId] = nodeId;

    public bool TryRemoveLocalNodeIdIfMatch(ulong networkId, ulong expectedNodeId)
    {
        if (!_localNodeIds.TryGetValue(networkId, out var localNodeId) || localNodeId != expectedNodeId)
        {
            return false;
        }

        _localNodeIds.TryRemove(networkId, out _);
        RemoveFromDirectory(networkId, expectedNodeId);
        return true;
    }

    public bool TryGetPeers(ulong networkId, out ConcurrentDictionary<ulong, IPEndPoint> peers)
        => _networkPeers.TryGetValue(networkId, out peers!);

    public void RemoveNetworkPeers(ulong networkId)
        => _networkPeers.TryRemove(networkId, out _);

    public void AddOrUpdatePeer(ulong networkId, ulong nodeId, IPEndPoint endpoint)
    {
        var normalized = _normalizeEndpoint(endpoint);
        var peers = _networkPeers.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        peers[nodeId] = normalized;
    }

    public IEnumerable<KeyValuePair<ulong, IPEndPoint>> RegisterLocalAndGetKnownPeers(
        ulong networkId,
        ulong localNodeId,
        IPEndPoint advertisedEndpoint)
    {
        SetLocalNodeId(networkId, localNodeId);

        if (!_enablePeerDiscovery)
        {
            return Array.Empty<KeyValuePair<ulong, IPEndPoint>>();
        }

        var normalizedAdvertisedEndpoint = _normalizeEndpoint(advertisedEndpoint);
        var discoveredPeers = s_networkDirectory.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        discoveredPeers[localNodeId] = normalizedAdvertisedEndpoint;

        var localPeers = _networkPeers.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        foreach (var peer in discoveredPeers)
        {
            if (peer.Key == localNodeId)
            {
                continue;
            }

            localPeers[peer.Key] = peer.Value;
        }

        return discoveredPeers.Where(p => p.Key != localNodeId).ToArray();
    }

    public void RegisterDiscoveredPeer(ulong networkId, ulong sourceNodeId, IPEndPoint remoteEndpoint)
    {
        if (!_enablePeerDiscovery)
        {
            return;
        }

        var endpoint = _normalizeEndpoint(remoteEndpoint);
        var peers = _networkPeers.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        peers[sourceNodeId] = endpoint;
        var directoryPeers = s_networkDirectory.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        directoryPeers[sourceNodeId] = endpoint;
    }

    public void Cleanup()
    {
        foreach (var local in _localNodeIds)
        {
            RemoveFromDirectory(local.Key, local.Value);
        }

        _networkPeers.Clear();
        _localNodeIds.Clear();
    }

    private static void RemoveFromDirectory(ulong networkId, ulong nodeId)
    {
        if (s_networkDirectory.TryGetValue(networkId, out var discoveredPeers))
        {
            discoveredPeers.TryRemove(nodeId, out _);
            if (discoveredPeers.IsEmpty)
            {
                s_networkDirectory.TryRemove(networkId, out _);
            }
        }
    }
}
