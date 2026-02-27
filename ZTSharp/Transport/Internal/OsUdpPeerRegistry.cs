using System;
using System.Collections.Concurrent;
using System.Net;
using System.Linq;

namespace ZTSharp.Transport.Internal;

internal sealed class OsUdpPeerRegistry
{
    internal readonly record struct PeerEntry(IPEndPoint Endpoint, long LastSeenTicks);

    private const int DirectoryMaxNetworks = 256;
    internal const int DirectoryMaxPeersPerNetwork = 1024;
    internal static readonly TimeSpan DirectoryPeerTtl = TimeSpan.FromMinutes(5);
    private static readonly long DirectoryPeerTtlTicks = DirectoryPeerTtl.Ticks;

    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, PeerEntry>> s_networkDirectory = new();

    private readonly bool _enablePeerDiscovery;
    private readonly Func<IPEndPoint, IPEndPoint> _normalizeEndpoint;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, PeerEntry>> _networkPeers = new();
    private readonly ConcurrentDictionary<ulong, ulong> _localNodeIds = new();

    public OsUdpPeerRegistry(bool enablePeerDiscovery, Func<IPEndPoint, IPEndPoint> normalizeEndpoint, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(normalizeEndpoint);

        _enablePeerDiscovery = enablePeerDiscovery;
        _normalizeEndpoint = normalizeEndpoint;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    public bool TryGetPeers(ulong networkId, out ConcurrentDictionary<ulong, PeerEntry> peers)
    {
        if (!_networkPeers.TryGetValue(networkId, out peers!))
        {
            return false;
        }

        var nowTicks = GetNowTicks();
        ImportDirectoryPeers(networkId, peers, nowTicks);
        EvictExpiredAndTrimNetworkPeers(networkId, peers, nowTicks);
        return true;
    }

    public void RemoveNetworkPeers(ulong networkId)
        => _networkPeers.TryRemove(networkId, out _);

    public void RefreshPeerLastSeen(ulong networkId, ulong nodeId)
    {
        if (!_networkPeers.TryGetValue(networkId, out var peers))
        {
            return;
        }

        var nowTicks = GetNowTicks();
        while (peers.TryGetValue(nodeId, out var existing))
        {
            if (existing.LastSeenTicks >= nowTicks)
            {
                return;
            }

            var updated = existing with { LastSeenTicks = nowTicks };
            if (peers.TryUpdate(nodeId, updated, existing))
            {
                return;
            }
        }
    }

    public void AddOrUpdatePeer(ulong networkId, ulong nodeId, IPEndPoint endpoint)
    {
        var nowTicks = GetNowTicks();
        var normalized = _normalizeEndpoint(endpoint);
        var peers = _networkPeers.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, PeerEntry>());
        peers[nodeId] = new PeerEntry(normalized, nowTicks);
        SweepNetworkPeers(nowTicks);
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

        var nowTicks = GetNowTicks();
        var normalizedAdvertisedEndpoint = _normalizeEndpoint(advertisedEndpoint);
        var discoveredPeers = s_networkDirectory.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, PeerEntry>());
        discoveredPeers[localNodeId] = new PeerEntry(normalizedAdvertisedEndpoint, nowTicks);
        SweepDirectory(nowTicks);

        var localPeers = _networkPeers.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, PeerEntry>());
        foreach (var peer in discoveredPeers)
        {
            if (peer.Key == localNodeId)
            {
                continue;
            }

            localPeers[peer.Key] = new PeerEntry(peer.Value.Endpoint, nowTicks);
        }

        return discoveredPeers
            .Where(p => p.Key != localNodeId)
            .Select(p => new KeyValuePair<ulong, IPEndPoint>(p.Key, p.Value.Endpoint))
            .ToArray();
    }

    public void RefreshLocalRegistration(ulong networkId, ulong localNodeId, IPEndPoint advertisedEndpoint)
    {
        if (!_enablePeerDiscovery)
        {
            return;
        }

        if (!_localNodeIds.TryGetValue(networkId, out var registeredNodeId) || registeredNodeId != localNodeId)
        {
            return;
        }

        var nowTicks = GetNowTicks();
        var normalizedAdvertisedEndpoint = _normalizeEndpoint(advertisedEndpoint);
        var discoveredPeers = s_networkDirectory.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, PeerEntry>());
        discoveredPeers[localNodeId] = new PeerEntry(normalizedAdvertisedEndpoint, nowTicks);
        SweepDirectory(nowTicks);
    }

    public void Cleanup()
    {
        var nowTicks = GetNowTicks();
        foreach (var local in _localNodeIds)
        {
            RemoveFromDirectory(local.Key, local.Value);
        }

        SweepDirectory(nowTicks);
        SweepNetworkPeers(nowTicks);
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

    private long GetNowTicks()
        => _timeProvider.GetUtcNow().UtcDateTime.Ticks;

    private static void EvictExpiredAndTrimPeers(ConcurrentDictionary<ulong, PeerEntry> peers, long nowTicks)
    {
        var cutoffTicks = nowTicks - DirectoryPeerTtlTicks;
        foreach (var peer in peers)
        {
            if (peer.Value.LastSeenTicks < cutoffTicks)
            {
                peers.TryRemove(peer.Key, out _);
            }
        }

        if (peers.Count > DirectoryMaxPeersPerNetwork)
        {
            var removeCount = peers.Count - DirectoryMaxPeersPerNetwork;
            var toRemove = peers
                .OrderBy(p => p.Value.LastSeenTicks)
                .Take(removeCount)
                .Select(p => p.Key)
                .ToArray();

            foreach (var key in toRemove)
            {
                peers.TryRemove(key, out _);
            }
        }
    }

    private void EvictExpiredAndTrimNetworkPeers(ulong networkId, ConcurrentDictionary<ulong, PeerEntry> peers, long nowTicks)
    {
        EvictExpiredAndTrimPeers(peers, nowTicks);
        if (peers.IsEmpty && (!_localNodeIds.TryGetValue(networkId, out var localNodeId) || localNodeId == 0))
        {
            _networkPeers.TryRemove(networkId, out _);
        }
    }

    private void SweepNetworkPeers(long nowTicks)
    {
        foreach (var network in _networkPeers)
        {
            EvictExpiredAndTrimNetworkPeers(network.Key, network.Value, nowTicks);
        }

        if (_networkPeers.Count <= DirectoryMaxNetworks)
        {
            return;
        }

        var overflow = _networkPeers.Count - DirectoryMaxNetworks;
        var toRemove = _networkPeers
            .Select(network =>
            {
                var lastSeen = 0L;
                foreach (var peer in network.Value.Values)
                {
                    if (peer.LastSeenTicks > lastSeen)
                    {
                        lastSeen = peer.LastSeenTicks;
                    }
                }

                return (NetworkId: network.Key, LastSeenTicks: lastSeen);
            })
            .OrderBy(network => network.LastSeenTicks)
            .Take(overflow)
            .Select(network => network.NetworkId)
            .ToArray();

        foreach (var networkId in toRemove)
        {
            _networkPeers.TryRemove(networkId, out _);
        }
    }

    private static void SweepDirectory(long nowTicks)
    {
        foreach (var network in s_networkDirectory)
        {
            EvictExpiredAndTrimPeers(network.Value, nowTicks);
            if (network.Value.IsEmpty)
            {
                s_networkDirectory.TryRemove(network.Key, out _);
            }
        }

        if (s_networkDirectory.Count <= DirectoryMaxNetworks)
        {
            return;
        }

        var overflow = s_networkDirectory.Count - DirectoryMaxNetworks;
        var toRemove = s_networkDirectory
            .Select(network =>
            {
                var lastSeen = 0L;
                foreach (var peer in network.Value.Values)
                {
                    if (peer.LastSeenTicks > lastSeen)
                    {
                        lastSeen = peer.LastSeenTicks;
                    }
                }

                return (NetworkId: network.Key, LastSeenTicks: lastSeen);
            })
            .OrderBy(network => network.LastSeenTicks)
            .Take(overflow)
            .Select(network => network.NetworkId)
            .ToArray();

        foreach (var networkId in toRemove)
        {
            s_networkDirectory.TryRemove(networkId, out _);
        }
    }

    private void ImportDirectoryPeers(ulong networkId, ConcurrentDictionary<ulong, PeerEntry> peers, long nowTicks)
    {
        if (!_enablePeerDiscovery)
        {
            return;
        }

        if (!s_networkDirectory.TryGetValue(networkId, out var directoryPeers))
        {
            return;
        }

        EvictExpiredAndTrimPeers(directoryPeers, nowTicks);
        if (directoryPeers.IsEmpty)
        {
            s_networkDirectory.TryRemove(networkId, out _);
            return;
        }

        _localNodeIds.TryGetValue(networkId, out var localNodeId);
        foreach (var peer in directoryPeers)
        {
            if (peer.Key == localNodeId)
            {
                continue;
            }

            peers[peer.Key] = new PeerEntry(peer.Value.Endpoint, nowTicks);
        }
    }

    internal static void ClearDirectoryForTests()
        => s_networkDirectory.Clear();

    internal static int GetDirectoryPeerCountForTests(ulong networkId)
        => s_networkDirectory.TryGetValue(networkId, out var peers) ? peers.Count : 0;
}
