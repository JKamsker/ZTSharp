using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using ZTSharp.Transport;

namespace ZTSharp.Internal;

internal sealed class NodeNetworkService
{
    private readonly IStateStore _store;
    private readonly INodeTransport _transport;
    private readonly NodeEventStream _events;
    private readonly NodePeerService _peerService;

    private readonly ConcurrentDictionary<ulong, NetworkInfo> _joinedNetworks = new();
    private readonly ConcurrentDictionary<ulong, Guid> _networkRegistrations = new();
    private readonly NetworkIdReadOnlyCollection _joinedNetworkIds;

    public NodeNetworkService(IStateStore store, INodeTransport transport, NodeEventStream events, NodePeerService peerService)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _peerService = peerService ?? throw new ArgumentNullException(nameof(peerService));
        _joinedNetworkIds = new NetworkIdReadOnlyCollection(_joinedNetworks);
    }

    public Task<IReadOnlyCollection<ulong>> GetNetworksAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<ulong>>(_joinedNetworkIds);
    }

    public async Task JoinNetworkAsync(
        ulong networkId,
        ulong localNodeId,
        IPEndPoint? localEndpoint,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> onFrameReceived,
        CancellationToken cancellationToken)
    {
        _events.Publish(EventCode.NetworkJoinRequested, DateTimeOffset.UtcNow, networkId);

        Guid registration = default;
        var now = DateTimeOffset.UtcNow;
        var key = BuildNetworkFileKey(networkId);
        try
        {
            registration = await _transport.JoinNetworkAsync(
                networkId,
                localNodeId,
                onFrameReceived,
                localEndpoint,
                cancellationToken).ConfigureAwait(false);

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new NetworkState(networkId, now, NetworkStateState.Joined),
                JsonContext.Default.NetworkState);

            await _store.WriteAsync(key, payload, cancellationToken).ConfigureAwait(false);
            _joinedNetworks[networkId] = new NetworkInfo(networkId, now);
            _networkRegistrations[networkId] = registration;
            await _peerService.RecoverPeersAsync(networkId, _transport, cancellationToken).ConfigureAwait(false);

            _events.Publish(EventCode.NetworkJoined, DateTimeOffset.UtcNow, networkId);
        }
        catch
        {
            if (registration != default)
            {
                try
                {
                    await _transport.LeaveNetworkAsync(networkId, registration, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            throw;
        }
    }

    public async Task LeaveNetworkAsync(ulong networkId, CancellationToken cancellationToken)
    {
        var key = BuildNetworkFileKey(networkId);
        if (_networkRegistrations.TryRemove(networkId, out var registration))
        {
            await _transport.LeaveNetworkAsync(networkId, registration, cancellationToken).ConfigureAwait(false);
        }

        var removed = _joinedNetworks.TryRemove(networkId, out _);
        if (removed)
        {
            await _store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            _events.Publish(EventCode.NetworkLeft, DateTimeOffset.UtcNow, networkId);
        }
    }

    public async Task SendFrameAsync(ulong networkId, ulong localNodeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!_joinedNetworks.ContainsKey(networkId))
        {
            throw new InvalidOperationException($"Node is not a member of network {networkId}.");
        }

        await _transport.SendFrameAsync(networkId, localNodeId, payload, cancellationToken).ConfigureAwait(false);
        _events.Publish(EventCode.NetworkFrameSent, DateTimeOffset.UtcNow, networkId, "Frame sent");
    }

    public async Task<IReadOnlyList<NetworkAddress>> GetNetworkAddressesAsync(ulong networkId, CancellationToken cancellationToken)
    {
        if (!_joinedNetworks.ContainsKey(networkId))
        {
            throw new InvalidOperationException($"Node is not a member of network {networkId}.");
        }

        var key = BuildNetworkAddressesFileKey(networkId);
        var payload = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (!payload.HasValue || payload.Value.Length == 0)
        {
            return Array.Empty<NetworkAddress>();
        }

        if (!NetworkAddressCodec.TryDecode(payload.Value.Span, out var addresses))
        {
            throw new InvalidOperationException($"Invalid address state payload for {key}.");
        }

        return addresses;
    }

    public async Task SetNetworkAddressesAsync(
        ulong networkId,
        IReadOnlyList<NetworkAddress> addresses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (!_joinedNetworks.ContainsKey(networkId))
        {
            throw new InvalidOperationException($"Node is not a member of network {networkId}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var key = BuildNetworkAddressesFileKey(networkId);
        var requiredLength = NetworkAddressCodec.GetEncodedLength(addresses);
        var buffer = ArrayPool<byte>.Shared.Rent(requiredLength);
        try
        {
            if (!NetworkAddressCodec.TryEncode(addresses, buffer, out var bytesWritten))
            {
                throw new InvalidOperationException("Failed to encode network addresses.");
            }

            await _store.WriteAsync(key, buffer.AsMemory(0, bytesWritten), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task RecoverNetworksAsync(
        ulong localNodeId,
        IPEndPoint? localEndpoint,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> onFrameReceived,
        CancellationToken cancellationToken)
    {
        var keys = await _store.ListAsync(NodeStoreKeys.NetworksDirectoryPrefix, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            var relative = key;
            if (!relative.StartsWith(NodeStoreKeys.NetworksDirectoryPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = relative.AsSpan(NodeStoreKeys.NetworksDirectoryPrefix.Length);
            if (!suffix.EndsWith(".conf", StringComparison.Ordinal))
            {
                continue;
            }

            var networkIdText = suffix[..^5];
            if (ulong.TryParse(networkIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var networkId))
            {
                _joinedNetworks.TryAdd(networkId, new NetworkInfo(networkId, DateTimeOffset.UtcNow));
            }
        }

        foreach (var network in _joinedNetworks.Keys)
        {
            var registration = await _transport.JoinNetworkAsync(
                network,
                localNodeId,
                onFrameReceived,
                localEndpoint,
                cancellationToken).ConfigureAwait(false);
            _networkRegistrations[network] = registration;
            await _peerService.RecoverPeersAsync(network, _transport, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task LeaveAllNetworksAsync()
    {
        await UnregisterAllNetworksAsync(CancellationToken.None).ConfigureAwait(false);
        _joinedNetworks.Clear();
    }

    public async Task UnregisterAllNetworksAsync(CancellationToken cancellationToken)
    {
        foreach (var kv in _networkRegistrations)
        {
            await _transport.LeaveNetworkAsync(kv.Key, kv.Value, cancellationToken).ConfigureAwait(false);
        }

        _networkRegistrations.Clear();
    }

    private static string BuildNetworkFileKey(ulong networkId) => $"{NodeStoreKeys.NetworksDirectory}/{networkId}.conf";

    private static string BuildNetworkAddressesFileKey(ulong networkId)
        => $"{NodeStoreKeys.NetworksDirectory}/{networkId}{NodeStoreKeys.NetworkAddressesSuffix}";
}
