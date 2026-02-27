using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ZTSharp.Transport.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneRuntime : IAsyncDisposable
{
    private const long DirectEndpointManagerTtlMs = 600_000;

    private readonly IZeroTierUdpTransport _udp;
    private readonly NodeId _rootNodeId;
    private readonly IPEndPoint _rootEndpoint;
    private readonly byte[] _rootKey;
    private readonly byte _rootProtocolVersion;
    private readonly ZeroTierIdentity _localIdentity;
    private readonly ulong _networkId;
    private readonly byte[] _inlineCom;
    private readonly IPAddress[] _localManagedIpsV4;
    private readonly byte[][] _localManagedIpsV4Bytes;
    private readonly IPAddress[] _localManagedIpsV6;
    private readonly ZeroTierMac _localMac;
    private readonly ConcurrentDictionary<NodeId, ZeroTierDirectEndpointManager> _directEndpoints = new();
    private readonly ConcurrentDictionary<NodeId, long> _directEndpointLastUsedMs = new();
    private long _lastDirectEndpointCleanupMs;
    private readonly ZeroTierPeerPhysicalPathTracker _peerPaths;
    private readonly ZeroTierPeerEchoManager _peerEcho;
    private readonly ZeroTierExternalSurfaceAddressTracker _surfaceAddresses;
    private readonly ZeroTierPeerQosManager _peerQos;
    private readonly ZeroTierPeerPathNegotiationManager _peerNegotiation;
    private readonly ZeroTierPeerBondPolicyEngine _bondEngine;
    private readonly ZeroTierMultipathOptions _multipath;

    private readonly Channel<ZeroTierUdpDatagram> _peerQueue = Channel.CreateBounded<ZeroTierUdpDatagram>(new BoundedChannelOptions(capacity: 2048)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    private long _peerQueueDropCount;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _dispatcherLoop;
    private readonly Task _peerLoop;
    private readonly Task? _multipathMaintenanceLoop;

    private readonly ZeroTierDataplaneRootClient _rootClient;
    private readonly ZeroTierDataplanePeerSecurity _peerSecurity;
    private readonly ManagedIpToNodeIdCache _managedIpToNodeId = new();
    private readonly ZeroTierDataplaneRouteRegistry _routes;
    private readonly ZeroTierDataplanePeerPacketHandler _peerPackets;
    private readonly ZeroTierDataplanePeerDatagramProcessor _peerDatagrams;
    private readonly ZeroTierDataplaneRxLoops _rxLoops;

    private bool _disposed;

    public ZeroTierDataplaneRuntime(
        IZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        byte rootProtocolVersion,
        ZeroTierIdentity localIdentity,
        ulong networkId,
        IReadOnlyList<IPAddress> localManagedIpsV4,
        IReadOnlyList<IPAddress> localManagedIpsV6,
        byte[] inlineCom)
        : this(
            udp,
            rootNodeId,
            rootEndpoint,
            rootKey,
            rootProtocolVersion,
            localIdentity,
            networkId,
            localManagedIpsV4,
            localManagedIpsV6,
            inlineCom,
            multipath: new ZeroTierMultipathOptions())
    {
    }

    public ZeroTierDataplaneRuntime(
        IZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        byte rootProtocolVersion,
        ZeroTierIdentity localIdentity,
        ulong networkId,
        IReadOnlyList<IPAddress> localManagedIpsV4,
        IReadOnlyList<IPAddress> localManagedIpsV6,
        byte[] inlineCom,
        ZeroTierMultipathOptions multipath)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(localManagedIpsV6);
        ArgumentNullException.ThrowIfNull(inlineCom);
        ArgumentNullException.ThrowIfNull(multipath);

        // Some local test harnesses pass wildcard-bound socket endpoints (0.0.0.0/::) as a "reachable" root endpoint.
        // Normalize those to loopback to keep the dataplane's remote root endpoint concrete.
        rootEndpoint = UdpEndpointNormalization.NormalizeForAdvertisement(rootEndpoint);

        if (localManagedIpsV4.Count == 0 && localManagedIpsV6.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localManagedIpsV4), "At least one managed IP (IPv4 or IPv6) is required.");
        }

        for (var i = 0; i < localManagedIpsV4.Count; i++)
        {
            if (localManagedIpsV4[i].AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentOutOfRangeException(nameof(localManagedIpsV4), "All IPv4 managed IPs must be IPv4.");
            }
        }

        for (var i = 0; i < localManagedIpsV6.Count; i++)
        {
            if (localManagedIpsV6[i].AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw new ArgumentOutOfRangeException(nameof(localManagedIpsV6), "All IPv6 managed IPs must be IPv6.");
            }
        }

        _udp = udp;
        _rootNodeId = rootNodeId;
        _rootEndpoint = rootEndpoint;
        _rootKey = rootKey;
        _rootProtocolVersion = rootProtocolVersion;
        _localIdentity = localIdentity;
        _networkId = networkId;
        _inlineCom = inlineCom;
        _multipath = multipath;
        _localManagedIpsV4 = localManagedIpsV4.Count == 0 ? Array.Empty<IPAddress>() : localManagedIpsV4.ToArray();
        _localManagedIpsV4Bytes = _localManagedIpsV4.Length == 0
            ? Array.Empty<byte[]>()
            : _localManagedIpsV4.Select(ip => ip.GetAddressBytes()).ToArray();
        _localManagedIpsV6 = localManagedIpsV6.Count == 0 ? Array.Empty<IPAddress>() : localManagedIpsV6.ToArray();
        _localMac = ZeroTierMac.FromAddress(localIdentity.NodeId, networkId);

        _routes = new ZeroTierDataplaneRouteRegistry(this);
        _rootClient = new ZeroTierDataplaneRootClient(
            udp,
            rootNodeId,
            rootEndpoint,
            rootKey,
            rootProtocolVersion,
            localIdentity.NodeId,
            networkId,
            inlineCom);
        _peerSecurity = new ZeroTierDataplanePeerSecurity(udp, _rootClient, localIdentity);
        _peerPaths = new ZeroTierPeerPhysicalPathTracker(ttl: TimeSpan.FromSeconds(30));
        _peerEcho = new ZeroTierPeerEchoManager(udp, localIdentity.NodeId, _peerSecurity.GetPeerProtocolVersionOrDefault);
        _surfaceAddresses = new ZeroTierExternalSurfaceAddressTracker(ttl: TimeSpan.FromMinutes(30));
        _peerQos = new ZeroTierPeerQosManager();
        _peerNegotiation = new ZeroTierPeerPathNegotiationManager();
        _bondEngine = new ZeroTierPeerBondPolicyEngine(GetPathLatencyMsOrNull, GetRemoteUtilityOrZero);

        var icmpv6 = new ZeroTierDataplaneIcmpv6Handler(this, _localMac, _localManagedIpsV6, _managedIpToNodeId);
        var ip = new ZeroTierDataplaneIpHandler(
            sender: this,
            routes: _routes,
            managedIpToNodeId: _managedIpToNodeId,
            icmpv6: icmpv6,
            networkId: _networkId,
            localMac: _localMac,
            localManagedIpsV4: _localManagedIpsV4,
            localManagedIpsV4Bytes: _localManagedIpsV4Bytes,
            localManagedIpsV6: _localManagedIpsV6);
        _peerPackets = new ZeroTierDataplanePeerPacketHandler(_networkId, _localMac, ip, handleControlAsync: HandlePeerControlPacketAsync);
        _peerDatagrams = new ZeroTierDataplanePeerDatagramProcessor(
            localIdentity.NodeId,
            _peerSecurity,
            _peerPackets,
            _peerPaths,
            _peerEcho,
            _surfaceAddresses,
            _peerQos,
            _peerNegotiation,
            multipath.Enabled);
        _rxLoops = new ZeroTierDataplaneRxLoops(
            _udp,
            _rootNodeId,
            _rootEndpoint,
            _rootKey,
            _localIdentity.NodeId,
            _rootClient,
            _peerDatagrams,
            acceptDirectPeerDatagrams: multipath.Enabled,
            handleRootControlAsync: HandleRootControlPacketAsync,
            onPeerQueueDrop: () =>
            {
                Interlocked.Increment(ref _peerQueueDropCount);
                if (ZeroTierTrace.Enabled)
                {
                    ZeroTierTrace.WriteLine("[zerotier] Drop: peer queue is full.");
                }
            });

        _dispatcherLoop = Task.Run(() => _rxLoops.DispatcherLoopAsync(_peerQueue, _cts.Token), CancellationToken.None);
        _peerLoop = Task.Run(() => _rxLoops.PeerLoopAsync(_peerQueue.Reader, _cts.Token), CancellationToken.None);
        _multipathMaintenanceLoop = multipath.Enabled
            ? Task.Run(() => MultipathMaintenanceLoopAsync(_cts.Token), CancellationToken.None)
            : null;
    }

    public NodeId NodeId => _localIdentity.NodeId;

    public IPEndPoint LocalUdp => _udp.LocalSockets[0].LocalEndpoint;

    public long PeerQueueDropCount => Interlocked.Read(ref _peerQueueDropCount);

    public IZeroTierRoutedIpLink RegisterTcpRoute(NodeId peerNodeId, IPEndPoint localEndpoint, IPEndPoint remoteEndpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _routes.RegisterTcpRoute(peerNodeId, localEndpoint, remoteEndpoint);
    }

    public void UnregisterRoute(ZeroTierTcpRouteKey routeKey)
        => _routes.UnregisterRoute(routeKey);

    public void UnregisterRoute(ZeroTierTcpRouteKeyV6 routeKey)
        => _routes.UnregisterRoute(routeKey);

    public bool TryRegisterTcpListener(
        IPAddress localAddress,
        ushort localPort,
        Func<NodeId, ReadOnlyMemory<byte>, CancellationToken, Task> onSyn)
        => _routes.TryRegisterTcpListener(localAddress, localPort, onSyn);

    public void UnregisterTcpListener(IPAddress localAddress, ushort localPort)
        => _routes.UnregisterTcpListener(localAddress, localPort);

    public bool TryRegisterUdpPort(AddressFamily addressFamily, ushort localPort, ChannelWriter<ZeroTierRoutedIpPacket> handler)
        => _routes.TryRegisterUdpPort(addressFamily, localPort, handler);

    public void UnregisterUdpPort(AddressFamily addressFamily, ushort localPort)
        => _routes.UnregisterUdpPort(addressFamily, localPort);

    public async Task<NodeId> ResolveNodeIdAsync(IPAddress managedIp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(managedIp);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _rootClient.ResolveNodeIdAsync(managedIp, _managedIpToNodeId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendIpv4Async(NodeId peerNodeId, ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = await GetPeerKeyAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
        var peerProtocolVersion = _peerSecurity.GetPeerProtocolVersionOrDefault(peerNodeId);
        var remoteMac = ZeroTierMac.FromAddress(peerNodeId, _networkId);
        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var packet = ZeroTierExtFramePacketBuilder.BuildPacket(
            packetId,
            destination: peerNodeId,
            source: _localIdentity.NodeId,
            networkId: _networkId,
            inlineCom: _inlineCom,
            to: remoteMac,
            from: _localMac,
            etherType: ZeroTierFrameCodec.EtherTypeIpv4,
            frame: ipv4Packet.Span,
            sharedKey: key,
            remoteProtocolVersion: peerProtocolVersion);

        var flowId = _multipath.Enabled ? ZeroTierFlowId.Derive(ipv4Packet.Span) : 0;
        await SendToPeerAsync(peerNodeId, packet, flowId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendEthernetFrameAsync(
        NodeId peerNodeId,
        ushort etherType,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = await GetPeerKeyAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
        var peerProtocolVersion = _peerSecurity.GetPeerProtocolVersionOrDefault(peerNodeId);
        var remoteMac = ZeroTierMac.FromAddress(peerNodeId, _networkId);
        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var packet = ZeroTierExtFramePacketBuilder.BuildPacket(
            packetId,
            destination: peerNodeId,
            source: _localIdentity.NodeId,
            networkId: _networkId,
            inlineCom: _inlineCom,
            to: remoteMac,
            from: _localMac,
            etherType: etherType,
            frame: frame.Span,
            sharedKey: key,
            remoteProtocolVersion: peerProtocolVersion);

        var flowId = (_multipath.Enabled && (etherType == ZeroTierFrameCodec.EtherTypeIpv4 || etherType == ZeroTierFrameCodec.EtherTypeIpv6))
            ? ZeroTierFlowId.Derive(frame.Span)
            : 0;

        await SendToPeerAsync(peerNodeId, packet, flowId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendToPeerAsync(
        NodeId peerNodeId,
        ReadOnlyMemory<byte> packet,
        uint flowId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_multipath.Enabled)
        {
            await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_multipath.BondPolicy == ZeroTierBondPolicy.Broadcast)
        {
            await SendToPeerBroadcastAsync(peerNodeId, packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TrySelectDirectPath(peerNodeId, flowId, out var direct))
        {
            await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        var parsedOk = TryGetPacketIdAndVerb(packet, out var parsed);
        var shouldRecord = parsedOk && parsed.Verb != ZeroTierVerb.QosMeasurement;

        var confirmed = _peerEcho.TryGetLastRttMs(peerNodeId, direct.LocalSocketId, direct.RemoteEndPoint, out _);
        if (_multipath.WarmupDuplicateToRoot && !confirmed)
        {
            if (shouldRecord)
            {
                _peerQos.RecordOutgoingPacket(peerNodeId, direct.LocalSocketId, direct.RemoteEndPoint, parsed.PacketId);
            }

            Task directSend = Task.CompletedTask;
            Task rootSend = Task.CompletedTask;
            try
            {
                directSend = _udp.SendAsync(direct.LocalSocketId, direct.RemoteEndPoint, packet, cancellationToken);
                rootSend = _udp.SendAsync(_rootEndpoint, packet, cancellationToken);
                await Task.WhenAll(directSend, rootSend).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException || (ex is AggregateException ae && ae.InnerExceptions.All(static inner => inner is SocketException)))
            {
                // Warm-up duplicates to root. If the direct send fails, forget any QoS state for it and
                // ensure a root send happens (if it hasn't already).
                // If only the root send fails, keep QoS state intact since the direct send may still succeed.
                var directOk = directSend.IsCompletedSuccessfully;
                var rootOk = rootSend.IsCompletedSuccessfully;

                if (!directOk && shouldRecord)
                {
                    _peerQos.ForgetOutgoingPacket(peerNodeId, direct.LocalSocketId, direct.RemoteEndPoint, parsed.PacketId);
                }

                if (!directOk && !rootOk)
                {
                    await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        if (shouldRecord)
        {
            _peerQos.RecordOutgoingPacket(peerNodeId, direct.LocalSocketId, direct.RemoteEndPoint, parsed.PacketId);
        }

        try
        {
            await _udp.SendAsync(direct.LocalSocketId, direct.RemoteEndPoint, packet, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            if (shouldRecord)
            {
                _peerQos.ForgetOutgoingPacket(peerNodeId, direct.LocalSocketId, direct.RemoteEndPoint, parsed.PacketId);
            }

            await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask SendToPeerBroadcastAsync(NodeId peerNodeId, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var observed = _peerPaths.GetSnapshot(peerNodeId);
        var hinted = observed.Length == 0 ? GetOrCreateDirectEndpointManager(peerNodeId).Endpoints : Array.Empty<IPEndPoint>();

        if (observed.Length == 0 && hinted.Length == 0)
        {
            await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        var parsedOk = TryGetPacketIdAndVerb(packet, out var parsed);
        var shouldRecord = parsedOk && parsed.Verb != ZeroTierVerb.QosMeasurement;

        var anyConfirmed = false;
        var directSuccess = 0;

        if (observed.Length > 0)
        {
            var broadcast = ZeroTierPeerBondPolicyEngine.GetBroadcastPaths(observed);
            for (var i = 0; i < broadcast.Length; i++)
            {
                var path = broadcast[i];
                if (_peerEcho.TryGetLastRttMs(peerNodeId, path.LocalSocketId, path.RemoteEndPoint, out _))
                {
                    anyConfirmed = true;
                }

                if (shouldRecord)
                {
                    _peerQos.RecordOutgoingPacket(peerNodeId, path.LocalSocketId, path.RemoteEndPoint, parsed.PacketId);
                }

                try
                {
                    await _udp.SendAsync(path.LocalSocketId, path.RemoteEndPoint, packet, cancellationToken).ConfigureAwait(false);
                    directSuccess++;
                }
                catch (SocketException)
                {
                    if (shouldRecord)
                    {
                        _peerQos.ForgetOutgoingPacket(peerNodeId, path.LocalSocketId, path.RemoteEndPoint, parsed.PacketId);
                    }
                }
            }
        }
        else
        {
            var localSockets = _udp.LocalSockets;
            for (var i = 0; i < hinted.Length; i++)
            {
                var localSocketId = localSockets.Count == 0 ? 0 : localSockets[i % localSockets.Count].Id;
                if (shouldRecord)
                {
                    _peerQos.RecordOutgoingPacket(peerNodeId, localSocketId, hinted[i], parsed.PacketId);
                }

                try
                {
                    await _udp.SendAsync(localSocketId, hinted[i], packet, cancellationToken).ConfigureAwait(false);
                    directSuccess++;
                }
                catch (SocketException)
                {
                    if (shouldRecord)
                    {
                        _peerQos.ForgetOutgoingPacket(peerNodeId, localSocketId, hinted[i], parsed.PacketId);
                    }
                }
            }
        }

        if (_multipath.WarmupDuplicateToRoot && !anyConfirmed)
        {
            await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (directSuccess == 0)
        {
            await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TrySelectDirectPath(NodeId peerNodeId, uint flowId, out ZeroTierSelectedPeerPath selected)
    {
        var observed = _peerPaths.GetSnapshot(peerNodeId);
        if (observed.Length > 0)
        {
            return _bondEngine.TrySelectSinglePath(peerNodeId, observed, flowId, _multipath.BondPolicy, out selected);
        }

        var hinted = GetOrCreateDirectEndpointManager(peerNodeId).Endpoints;
        if (hinted.Length > 0)
        {
            var localSockets = _udp.LocalSockets;
            var endpointIndex = hinted.Length == 1 ? 0 : (int)(flowId % (uint)hinted.Length);
            var socketIndex = localSockets.Count <= 1
                ? 0
                : (int)((flowId / (uint)hinted.Length) % (uint)localSockets.Count);
            var localSocketId = localSockets.Count == 0 ? 0 : localSockets[socketIndex].Id;
            selected = new ZeroTierSelectedPeerPath(localSocketId, hinted[endpointIndex]);
            return true;
        }

        selected = default;
        return false;
    }

    private int? GetPathLatencyMsOrNull(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint)
    {
        if (_peerEcho.TryGetLastRttMs(peerNodeId, localSocketId, remoteEndPoint, out var rttMs))
        {
            return rttMs;
        }

        if (_peerQos.TryGetLastLatencyAverageMs(peerNodeId, localSocketId, remoteEndPoint, out var latencyMs))
        {
            return (int)Math.Min((long)latencyMs * 2, int.MaxValue);
        }

        return null;
    }

    private short GetRemoteUtilityOrZero(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint)
        => _peerNegotiation.TryGetRemoteUtility(peerNodeId, localSocketId, remoteEndPoint, out var util) ? util : (short)0;

    private async Task MultipathMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunMultipathMaintenanceOnceAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Maintenance loop must survive per-iteration faults.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ZeroTierTrace.WriteLine($"[zerotier] Multipath maintenance fault: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task RunMultipathMaintenanceOnceAsync(CancellationToken cancellationToken)
    {
        var nowMs = Environment.TickCount64;
        _bondEngine.MaintenanceTick();
        CleanupDirectEndpointManagers(nowMs);

        var peers = _peerPaths.GetPeersSnapshot();
        if (peers.Length == 0)
        {
            return;
        }

        for (var i = 0; i < peers.Length; i++)
        {
            var peerNodeId = peers[i];
            if (!_peerSecurity.TryGetPeerKey(peerNodeId, out var key))
            {
                continue;
            }

            var peerProtocolVersion = _peerSecurity.GetPeerProtocolVersionOrDefault(peerNodeId);
            var paths = _peerPaths.GetSnapshot(peerNodeId);
            if (paths.Length == 0)
            {
                continue;
            }

            for (var p = 0; p < paths.Length; p++)
            {
                var path = paths[p];

                await _peerEcho
                    .TrySendEchoProbeAsync(peerNodeId, path.LocalSocketId, path.RemoteEndPoint, key, cancellationToken)
                    .ConfigureAwait(false);

                if (!_peerQos.TryBuildOutboundPayload(peerNodeId, path.LocalSocketId, path.RemoteEndPoint, out var qosPayload))
                {
                    continue;
                }

                await SendPeerControlAsync(
                        peerNodeId,
                        path.LocalSocketId,
                        path.RemoteEndPoint,
                        ZeroTierVerb.QosMeasurement,
                        qosPayload,
                        key,
                        peerProtocolVersion,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_multipath.BondPolicy != ZeroTierBondPolicy.ActiveBackup || paths.Length < 2)
            {
                continue;
            }

            var bestPathIndex = -1;
            var bestScore = int.MinValue;
            var secondScore = int.MinValue;

            for (var p = 0; p < paths.Length; p++)
            {
                var path = paths[p];
                var score = ComputePathQualityScore(peerNodeId, path.LocalSocketId, path.RemoteEndPoint);

                if (score > bestScore)
                {
                    secondScore = bestScore;
                    bestScore = score;
                    bestPathIndex = p;
                }
                else if (score > secondScore)
                {
                    secondScore = score;
                }
            }

            if (bestPathIndex < 0)
            {
                continue;
            }

            var bestPath = paths[bestPathIndex];
            if (!_peerNegotiation.TryMarkSent(peerNodeId, bestPath.LocalSocketId, bestPath.RemoteEndPoint))
            {
                continue;
            }

            var delta = bestScore - Math.Max(secondScore, 0);
            var utility = (short)Math.Clamp(delta, short.MinValue, short.MaxValue);
            var payload = new byte[2];
            BinaryPrimitives.WriteInt16BigEndian(payload, utility);

            await SendPeerControlAsync(
                    peerNodeId,
                    bestPath.LocalSocketId,
                    bestPath.RemoteEndPoint,
                    ZeroTierVerb.PathNegotiationRequest,
                    payload,
                    key,
                    peerProtocolVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private int ComputePathQualityScore(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint)
    {
        if (_peerEcho.TryGetLastRttMs(peerNodeId, localSocketId, remoteEndPoint, out var rttMs))
        {
            return 32767 - Math.Clamp(rttMs, 0, 32767);
        }

        if (_peerQos.TryGetLastLatencyAverageMs(peerNodeId, localSocketId, remoteEndPoint, out var latencyMs))
        {
            var estRtt = (int)Math.Min((long)latencyMs * 2, 32767L);
            return 32767 - estRtt;
        }

        return 0;
    }

    private async ValueTask SendPeerControlAsync(
        NodeId peerNodeId,
        int localSocketId,
        IPEndPoint remoteEndPoint,
        ZeroTierVerb verb,
        ReadOnlyMemory<byte> payload,
        byte[] sharedKey,
        byte remoteProtocolVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: peerNodeId,
            Source: _localIdentity.NodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)verb);

        var packet = ZeroTierPacketCodec.Encode(header, payload.Span);
        ZeroTierPacketCrypto.Armor(packet, ZeroTierPacketCrypto.SelectOutboundKey(sharedKey, remoteProtocolVersion), encryptPayload: false);
        await _udp.SendAsync(localSocketId, remoteEndPoint, packet, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetPacketIdAndVerb(ReadOnlyMemory<byte> packet, out (ulong PacketId, ZeroTierVerb Verb) parsed)
    {
        if (packet.Length < ZeroTierPacketHeader.Length)
        {
            parsed = default;
            return false;
        }

        var span = packet.Span;
        var packetId = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(ZeroTierPacketHeader.IndexPacketId, 8));
        var verb = (ZeroTierVerb)(span[ZeroTierPacketHeader.IndexVerb] & 0x1F);
        parsed = (packetId, verb);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _peerQueue.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_multipathMaintenanceLoop is not null)
        {
            try
            {
                await _multipathMaintenanceLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
        }

        await _udp.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _dispatcherLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException) when (_cts.IsCancellationRequested)
        {
        }

        try
        {
            await _peerLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException) when (_cts.IsCancellationRequested)
        {
        }

        _cts.Dispose();
        _peerSecurity.Dispose();
    }

    private Task<byte[]> GetPeerKeyAsync(NodeId peerNodeId, CancellationToken cancellationToken)
        => _peerSecurity.GetPeerKeyAsync(peerNodeId, cancellationToken);

    private ValueTask HandleRootControlPacketAsync(
        ZeroTierVerb verb,
        ReadOnlyMemory<byte> payload,
        IPEndPoint receivedVia,
        CancellationToken cancellationToken)
    {
        if (verb != ZeroTierVerb.Rendezvous)
        {
            return ValueTask.CompletedTask;
        }

        if (!ZeroTierRendezvousCodec.TryParse(payload.Span, out var rendezvous) || rendezvous.With.Value == 0)
        {
            return ValueTask.CompletedTask;
        }

        var directEndpoints = GetOrCreateDirectEndpointManager(rendezvous.With);
        return directEndpoints.HandleRendezvousFromRootAsync(payload, receivedVia, cancellationToken);
    }

    private ValueTask HandlePeerControlPacketAsync(
        NodeId peerNodeId,
        ZeroTierVerb verb,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (verb != ZeroTierVerb.PushDirectPaths)
        {
            return ValueTask.CompletedTask;
        }

        var directEndpoints = GetOrCreateDirectEndpointManager(peerNodeId);
        return directEndpoints.HandlePushDirectPathsFromRemoteAsync(payload, cancellationToken);
    }

    private ZeroTierDirectEndpointManager GetOrCreateDirectEndpointManager(NodeId peerNodeId)
    {
        _directEndpointLastUsedMs[peerNodeId] = Environment.TickCount64;
        return _directEndpoints.GetOrAdd(peerNodeId, id => new ZeroTierDirectEndpointManager(_udp, _rootEndpoint, id));
    }

    private void CleanupDirectEndpointManagers(long nowMs)
    {
        var last = Volatile.Read(ref _lastDirectEndpointCleanupMs);
        if (last != 0 && unchecked(nowMs - last) < 10_000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastDirectEndpointCleanupMs, nowMs, last) != last)
        {
            return;
        }

        foreach (var pair in _directEndpointLastUsedMs)
        {
            if (unchecked(nowMs - pair.Value) > DirectEndpointManagerTtlMs)
            {
                _directEndpointLastUsedMs.TryRemove(pair.Key, out _);
                _directEndpoints.TryRemove(pair.Key, out _);
            }
        }
    }

}
