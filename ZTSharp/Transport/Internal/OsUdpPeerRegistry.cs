using System;
using System.Collections.Concurrent;
using System.Net;
using System.Linq;

namespace ZTSharp.Transport.Internal;

internal sealed class OsUdpPeerRegistry
{
    private readonly record struct PeerDirectoryEntry(IPEndPoint Endpoint, long LastSeenTicks);

    private const int DirectoryMaxNetworks = 256;
    internal const int DirectoryMaxPeersPerNetwork = 1024;
    internal static readonly TimeSpan DirectoryPeerTtl = TimeSpan.FromMinutes(5);
    private static readonly long DirectoryPeerTtlTicks = DirectoryPeerTtl.Ticks;

    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, PeerDirectoryEntry>> s_networkDirectory = new();

    private readonly bool _enablePeerDiscovery;
    private readonly Func<IPEndPoint, IPEndPoint> _normalizeEndpoint;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, IPEndPoint>> _networkPeers = new();
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

        var nowTicks = GetNowTicks();
        var normalizedAdvertisedEndpoint = _normalizeEndpoint(advertisedEndpoint);
        var discoveredPeers = s_networkDirectory.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, PeerDirectoryEntry>());
        discoveredPeers[localNodeId] = new PeerDirectoryEntry(normalizedAdvertisedEndpoint, nowTicks);
        SweepDirectory(nowTicks);

        var localPeers = _networkPeers.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        foreach (var peer in discoveredPeers)
        {
            if (peer.Key == localNodeId)
            {
                continue;
            }

            localPeers[peer.Key] = peer.Value.Endpoint;
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
        var discoveredPeers = s_networkDirectory.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, PeerDirectoryEntry>());
        discoveredPeers[localNodeId] = new PeerDirectoryEntry(normalizedAdvertisedEndpoint, nowTicks);
        SweepDirectory(nowTicks);
    }

    public void RegisterDiscoveredPeer(ulong networkId, ulong sourceNodeId, IPEndPoint remoteEndpoint)
    {
        if (!_enablePeerDiscovery)
        {
            return;
        }

        var nowTicks = GetNowTicks();
        var endpoint = _normalizeEndpoint(remoteEndpoint);
        var peers = _networkPeers.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        peers[sourceNodeId] = endpoint;
        var directoryPeers = s_networkDirectory.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, PeerDirectoryEntry>());
        directoryPeers[sourceNodeId] = new PeerDirectoryEntry(endpoint, nowTicks);
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

    private static void EvictExpiredAndTrimNetwork(ulong networkId, ConcurrentDictionary<ulong, PeerDirectoryEntry> discoveredPeers, long nowTicks)
    {
        var cutoffTicks = nowTicks - DirectoryPeerTtlTicks;
        foreach (var peer in discoveredPeers)
        {
            if (peer.Value.LastSeenTicks < cutoffTicks)
            {
                discoveredPeers.TryRemove(peer.Key, out _);
            }
        }

        if (discoveredPeers.Count > DirectoryMaxPeersPerNetwork)
        {
            var removeCount = discoveredPeers.Count - DirectoryMaxPeersPerNetwork;
            var toRemove = discoveredPeers
                .OrderBy(p => p.Value.LastSeenTicks)
                .Take(removeCount)
                .Select(p => p.Key)
                .ToArray();

            foreach (var key in toRemove)
            {
                discoveredPeers.TryRemove(key, out _);
            }
        }

        if (discoveredPeers.IsEmpty)
        {
            s_networkDirectory.TryRemove(networkId, out _);
        }
    }

    private static void SweepDirectory(long nowTicks)
    {
        foreach (var network in s_networkDirectory)
        {
            EvictExpiredAndTrimNetwork(network.Key, network.Value, nowTicks);
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

    internal static void ClearDirectoryForTests()
        => s_networkDirectory.Clear();

    internal static int GetDirectoryPeerCountForTests(ulong networkId)
        => s_networkDirectory.TryGetValue(networkId, out var peers) ? peers.Count : 0;
}
