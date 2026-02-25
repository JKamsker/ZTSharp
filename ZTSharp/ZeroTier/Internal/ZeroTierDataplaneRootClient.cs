using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneRootClient
{
    private static readonly TimeSpan MulticastGatherTimeout = TimeSpan.FromSeconds(5);

    private readonly ZeroTierUdpTransport _udp;
    private readonly NodeId _rootNodeId;
    private readonly IPEndPoint _rootEndpoint;
    private readonly byte[] _rootKey;
    private readonly byte _rootProtocolVersion;
    private readonly NodeId _localNodeId;
    private readonly ulong _networkId;
    private readonly byte[] _inlineCom;

    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ZeroTierIdentity>> _pendingWhois = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<(uint TotalKnown, NodeId[] Members)>> _pendingGather = new();

    public ZeroTierDataplaneRootClient(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        byte rootProtocolVersion,
        NodeId localNodeId,
        ulong networkId,
        byte[] inlineCom)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(inlineCom);

        _udp = udp;
        _rootNodeId = rootNodeId;
        _rootEndpoint = rootEndpoint;
        _rootKey = rootKey;
        _rootProtocolVersion = rootProtocolVersion;
        _localNodeId = localNodeId;
        _networkId = networkId;
        _inlineCom = inlineCom;
    }

    public async Task<NodeId> ResolveNodeIdAsync(
        IPAddress managedIp,
        ConcurrentDictionary<IPAddress, NodeId> cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(managedIp);
        ArgumentNullException.ThrowIfNull(cache);
        cancellationToken.ThrowIfCancellationRequested();

        if (cache.TryGetValue(managedIp, out var cachedNodeId))
        {
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine($"[zerotier] Resolve {managedIp} -> {cachedNodeId} (cache).");
            }

            return cachedNodeId;
        }

        var group = ZeroTierMulticastGroup.DeriveForAddressResolution(managedIp);
        var (totalKnown, members) = await MulticastGatherAsync(group, gatherLimit: 32, cancellationToken).ConfigureAwait(false);
        if (members.Length == 0)
        {
            throw new InvalidOperationException($"Could not resolve '{managedIp}' to a ZeroTier node id (no multicast-gather results).");
        }

        var remoteNodeId = members[0];
        if (ZeroTierTrace.Enabled)
        {
            var list = string.Join(", ", members.Take(8).Select(member => member.ToString()));
            var suffix = members.Length > 8 ? ", ..." : string.Empty;
            ZeroTierTrace.WriteLine($"[zerotier] Resolve {managedIp} -> {remoteNodeId} (members: {members.Length}/{totalKnown}: {list}{suffix}; root: {_rootNodeId} via {_rootEndpoint}).");
        }

        return remoteNodeId;
    }

    public bool TryDispatchResponse(ZeroTierVerb verb, ReadOnlySpan<byte> payload)
    {
        switch (verb)
        {
            case ZeroTierVerb.Ok:
                {
                    if (payload.Length < 1 + 8)
                    {
                        return false;
                    }

                    var inReVerb = (ZeroTierVerb)(payload[0] & 0x1F);
                    var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1, 8));

                    if (inReVerb == ZeroTierVerb.Whois &&
                        _pendingWhois.TryRemove(inRePacketId, out var whoisTcs))
                    {
                        try
                        {
                            var identity = ZeroTierIdentityCodec.Deserialize(payload.Slice(1 + 8), out _);
                            whoisTcs.TrySetResult(identity);
                        }
                        catch (FormatException ex)
                        {
                            whoisTcs.TrySetException(ex);
                        }

                        return true;
                    }

                    if (inReVerb == ZeroTierVerb.MulticastGather &&
                        _pendingGather.TryRemove(inRePacketId, out var gatherTcs))
                    {
                        if (!ZeroTierMulticastGatherCodec.TryParseOkPayload(
                                payload.Slice(1 + 8),
                                out var okNetworkId,
                                out _,
                                out var totalKnown,
                                out var members) ||
                            okNetworkId != _networkId)
                        {
                            gatherTcs.TrySetException(new InvalidOperationException("Invalid MULTICAST_GATHER OK payload."));
                            return true;
                        }

                        gatherTcs.TrySetResult((totalKnown, members));
                        return true;
                    }

                    return false;
                }
            case ZeroTierVerb.Error:
                {
                    if (payload.Length < 1 + 8 + 1)
                    {
                        return false;
                    }

                    var inReVerb = (ZeroTierVerb)(payload[0] & 0x1F);
                    var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1, 8));
                    var errorCode = payload[1 + 8];
                    ulong? networkId = null;
                    if (payload.Length >= 1 + 8 + 1 + 8)
                    {
                        networkId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1 + 8 + 1, 8));
                    }

                    if (inReVerb == ZeroTierVerb.Whois &&
                        _pendingWhois.TryRemove(inRePacketId, out var whoisTcs))
                    {
                        whoisTcs.TrySetException(new InvalidOperationException(ZeroTierErrorFormatting.FormatError(inReVerb, errorCode, networkId)));
                        return true;
                    }

                    if (inReVerb == ZeroTierVerb.MulticastGather &&
                        _pendingGather.TryRemove(inRePacketId, out var gatherTcs))
                    {
                        gatherTcs.TrySetException(new InvalidOperationException(ZeroTierErrorFormatting.FormatError(inReVerb, errorCode, networkId)));
                        return true;
                    }

                    return false;
                }
            default:
                return false;
        }
    }

    public async Task<ZeroTierIdentity> WhoisAsync(NodeId targetNodeId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        var payload = new byte[5];
        ZeroTierBinaryPrimitives.WriteUInt40BigEndian(payload, targetNodeId.Value);
        return await SendRequestAsync(
            ZeroTierVerb.Whois,
            payload,
            _pendingWhois,
            timeout,
            "WHOIS",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(uint TotalKnown, NodeId[] Members)> MulticastGatherAsync(
        ZeroTierMulticastGroup group,
        uint gatherLimit,
        CancellationToken cancellationToken)
    {
        var payload = ZeroTierMulticastGatherCodec.EncodeRequestPayload(_networkId, group, gatherLimit, _inlineCom);
        return await SendRequestAsync(
            ZeroTierVerb.MulticastGather,
            payload,
            _pendingGather,
            MulticastGatherTimeout,
            "MULTICAST_GATHER",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendRequestAsync<TResponse>(
        ZeroTierVerb verb,
        byte[] payload,
        ConcurrentDictionary<ulong, TaskCompletionSource<TResponse>> pending,
        TimeSpan timeout,
        string operationName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: _rootNodeId,
            Source: _localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)verb);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, ZeroTierPacketCrypto.SelectOutboundKey(_rootKey, _rootProtocolVersion), encryptPayload: true);
        packetId = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(0, 8));

        var tcs = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(packetId, tcs))
        {
            throw new InvalidOperationException($"Packet id collision while sending {operationName}.");
        }

        try
        {
            await _udp.SendAsync(_rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
            return await ZeroTierTimeouts
                .RunWithTimeoutAsync(timeout, operation: $"{operationName} response", WaitForResponseAsync, cancellationToken)
                .ConfigureAwait(false);

            ValueTask<TResponse> WaitForResponseAsync(CancellationToken token) => new(tcs.Task.WaitAsync(token));
        }
        finally
        {
            pending.TryRemove(packetId, out _);
        }
    }

}
