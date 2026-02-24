using System.Collections.Concurrent;
using System.Collections;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Threading.Channels;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using JKamsker.LibZt.Transport;

namespace JKamsker.LibZt;

/// <summary>
/// Fully managed .NET node facade.
/// </summary>
public sealed class Node : IAsyncDisposable
{
    private const string IdentitySecretKey = "identity.secret";
    private const string IdentityPublicKey = "identity.public";
    private const string PlanetKey = "planet";
    private const string NetworksDirectory = "networks.d";
    private const string NetworksDirectoryPrefix = $"{NetworksDirectory}/";
    private const string NetworkAddressesSuffix = ".addr";
    private const string PeersDirectory = "peers.d";

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly IStateStore _store;
    private static readonly InMemoryNodeTransport SharedTransport = new();
    private readonly INodeTransport _transport;
    private readonly bool _ownsTransport;
    private readonly ILogger _logger;
    private readonly NodeOptions _options;
    private readonly ConcurrentDictionary<ulong, NetworkInfo> _joinedNetworks = new();
    private readonly ConcurrentDictionary<ulong, Guid> _networkRegistrations = new();
    private readonly NetworkIdReadOnlyCollection _joinedNetworkIds;
    private Channel<NodeEvent>? _events;
    private readonly CancellationTokenSource _nodeCts = new();

    private NodeState _state;
    private NodeId _nodeId;
    private bool _disposed;

    public Node(NodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateRootPath);

        _options = options;
        _store = options.StateStore ?? new FileStateStore(options.StateRootPath);
        _logger = (options.LoggerFactory ?? global::Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger<Node>();
        _transport = options.TransportMode == TransportMode.OsUdp
            ? new OsUdpNodeTransport(options.UdpListenPort ?? 0, options.EnableIpv6, options.EnablePeerDiscovery)
            : SharedTransport;
        _ownsTransport = options.TransportMode == TransportMode.OsUdp;
        _state = NodeState.Created;
        _nodeId = default;
        _joinedNetworkIds = new NetworkIdReadOnlyCollection(_joinedNetworks);
    }

    public event EventHandler<NodeEvent>? EventRaised;
    public event EventHandler<NetworkFrame>? FrameReceived;
    internal event RawFrameReceivedHandler? RawFrameReceived;

    public IStateStore Store => _store;

    public string StateRootPath => _options.StateRootPath;

    public IPEndPoint? LocalTransportEndpoint => GetLocalTransportEndpoint();

    public NodeId NodeId => _nodeId;

    public bool IsRunning => _state == NodeState.Running;

    public NodeState State => _state;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_state is NodeState.Running or NodeState.Starting)
            {
                return;
            }

            if (_state is NodeState.Stopping)
            {
                throw new InvalidOperationException("Cannot start while stopping.");
            }

            _state = NodeState.Starting;
            RaiseEvent(EventCode.NodeStarting, DateTimeOffset.UtcNow);

            var identity = await EnsureIdentityAsync(cancellationToken).ConfigureAwait(false);
            _nodeId = identity.NodeId;
            await RecoverNetworksAsync(cancellationToken).ConfigureAwait(false);
            _state = NodeState.Running;
            RaiseEvent(EventCode.NodeStarted, DateTimeOffset.UtcNow, message: "Node started");
        }
        catch (OperationCanceledException)
        {
            _state = NodeState.Faulted;
            throw;
        }
        catch (Exception ex)
        {
            _state = NodeState.Faulted;
#pragma warning disable CA1848
            _logger.LogError(ex, "Failed to start node");
#pragma warning restore CA1848
            RaiseEvent(EventCode.NodeFaulted, DateTimeOffset.UtcNow, message: ex.Message, error: ex);
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_state is NodeState.Stopped or NodeState.Faulted)
            {
                return;
            }

            _state = NodeState.Stopping;
            RaiseEvent(EventCode.NodeStopping, DateTimeOffset.UtcNow);

            await UnregisterAllNetworksAsync(cancellationToken).ConfigureAwait(false);
            await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _store.FlushAsync(cancellationToken).ConfigureAwait(false);
            _state = NodeState.Stopped;
            RaiseEvent(EventCode.NodeStopped, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _state = NodeState.Faulted;
            throw;
        }
        catch (Exception ex)
        {
            _state = NodeState.Faulted;
#pragma warning disable CA1848
            _logger.LogError(ex, "Failed to stop node");
#pragma warning restore CA1848
            RaiseEvent(EventCode.NodeFaulted, DateTimeOffset.UtcNow, message: ex.Message, error: ex);
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
    {
        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        RaiseEvent(EventCode.NetworkJoinRequested, DateTimeOffset.UtcNow, networkId);

        Guid registration = default;
        var now = DateTimeOffset.UtcNow;
        var key = BuildNetworkFileKey(networkId);
        try
        {
            var localEndpoint = GetLocalTransportEndpoint();
            registration = await _transport.JoinNetworkAsync(
                networkId,
                _nodeId.Value,
                OnFrameReceivedAsync,
                localEndpoint,
                cancellationToken).ConfigureAwait(false);

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new NetworkState(networkId, now, NetworkStateState.Joined),
                JsonContext.Default.NetworkState);

            await _store.WriteAsync(key, payload, cancellationToken).ConfigureAwait(false);
            _joinedNetworks[networkId] = new NetworkInfo(networkId, now);
            _networkRegistrations[networkId] = registration;
            await RecoverPeersAsync(networkId, cancellationToken).ConfigureAwait(false);

            RaiseEvent(EventCode.NetworkJoined, DateTimeOffset.UtcNow, networkId);
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

    public async Task LeaveNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
    {
        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        var key = BuildNetworkFileKey(networkId);
        if (_networkRegistrations.TryRemove(networkId, out var registration))
        {
            await _transport.LeaveNetworkAsync(networkId, registration, cancellationToken).ConfigureAwait(false);
        }

        var removed = _joinedNetworks.TryRemove(networkId, out _);
        if (removed)
        {
            await _store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            RaiseEvent(EventCode.NetworkLeft, DateTimeOffset.UtcNow, networkId);
        }
    }

    public async Task SendFrameAsync(ulong networkId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        if (!_joinedNetworks.ContainsKey(networkId))
        {
            throw new InvalidOperationException($"Node is not a member of network {networkId}.");
        }

        await _transport.SendFrameAsync(networkId, _nodeId.Value, payload, cancellationToken).ConfigureAwait(false);
        RaiseEvent(EventCode.NetworkFrameSent, DateTimeOffset.UtcNow, networkId, "Frame sent");
    }

    public Task<IReadOnlyCollection<ulong>> GetNetworksAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<ulong>>(_joinedNetworkIds);
    }

    public async Task<IReadOnlyList<NetworkAddress>> GetNetworkAddressesAsync(
        ulong networkId,
        CancellationToken cancellationToken = default)
    {
        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
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

    public IAsyncEnumerable<NodeEvent> GetEventStream(CancellationToken cancellationToken = default)
    {
        var channel = _events;
        if (channel is null)
        {
            var created = Channel.CreateUnbounded<NodeEvent>();
            channel = Interlocked.CompareExchange(ref _events, created, null) ?? created;
        }

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    public async Task AddPeerAsync(ulong networkId, ulong peerNodeId, IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (_transport is not OsUdpNodeTransport udpTransport)
        {
            throw new InvalidOperationException("Transport mode is not OS UDP.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await udpTransport.AddPeerAsync(networkId, peerNodeId, endpoint).ConfigureAwait(false);
        await PersistPeerAsync(networkId, peerNodeId, endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Identity> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        return await EnsureIdentityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _nodeCts.CancelAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        await LeaveAllNetworksAsync().ConfigureAwait(false);
        _disposed = true;
        if (_ownsTransport && _transport is IAsyncDisposable asyncTransport)
        {
            await asyncTransport.DisposeAsync().ConfigureAwait(false);
        }

        _stateLock.Dispose();
        _events?.Writer.TryComplete();
        _nodeCts.Dispose();
    }

    private async Task<Identity> EnsureIdentityAsync(CancellationToken cancellationToken)
    {
        var secret = await _store.ReadAsync(IdentitySecretKey, cancellationToken).ConfigureAwait(false);
        var publicKey = await _store.ReadAsync(IdentityPublicKey, cancellationToken).ConfigureAwait(false);

        if (secret.HasValue && secret.Value.Length == 32 && publicKey.HasValue && publicKey.Value.Length == 32)
        {
            return new Identity(
                new NodeId(ComputeNodeIdFromSecret(secret.Value.Span)),
                DateTimeOffset.UtcNow,
                publicKey.Value,
                secret.Value);
        }

        var createdSecret = RandomNumberGenerator.GetBytes(32);
        var createdPublicFull = SHA512.HashData(createdSecret.AsSpan(0, 32));
        var createdPublic = createdPublicFull.AsMemory(0, 32);
        var identity = new Identity(
            new NodeId(ComputeNodeIdFromSecret(createdSecret.AsSpan())),
            DateTimeOffset.UtcNow,
            createdPublic,
            createdSecret.AsMemory(0, 32));

        await _store.WriteAsync(IdentitySecretKey, identity.SecretKey, cancellationToken).ConfigureAwait(false);
        await _store.WriteAsync(IdentityPublicKey, identity.PublicKey, cancellationToken).ConfigureAwait(false);
        await _store.WriteAsync(PlanetKey, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        RaiseEvent(EventCode.IdentityInitialized, DateTimeOffset.UtcNow);
        return identity;
    }

    private async Task RecoverNetworksAsync(CancellationToken cancellationToken)
    {
        var keys = await _store.ListAsync(NetworksDirectory, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            var relative = key;
            if (!relative.StartsWith(NetworksDirectoryPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = relative.AsSpan(NetworksDirectoryPrefix.Length);
            if (!suffix.EndsWith(".conf", StringComparison.Ordinal))
            {
                continue;
            }

            var networkIdText = suffix[..^5];
            if (ulong.TryParse(networkIdText, out var networkId))
            {
                _joinedNetworks.TryAdd(networkId, new NetworkInfo(networkId, DateTimeOffset.UtcNow));
            }
        }

        foreach (var network in _joinedNetworks.Keys)
        {
            var localEndpoint = GetLocalTransportEndpoint();
            var registration = await _transport.JoinNetworkAsync(
                network,
                _nodeId.Value,
                OnFrameReceivedAsync,
                localEndpoint,
                cancellationToken).ConfigureAwait(false);
            _networkRegistrations[network] = registration;
            await RecoverPeersAsync(network, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecoverPeersAsync(ulong networkId, CancellationToken cancellationToken)
    {
        if (_transport is not OsUdpNodeTransport udpTransport)
        {
            return;
        }

        var prefix = BuildPeersNetworkPrefix(networkId);
        var keys = await _store.ListAsync(prefix, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParsePeerKey(prefix, key, out var peerNodeId))
            {
                continue;
            }

            var payload = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (!payload.HasValue || payload.Value.Length == 0)
            {
                continue;
            }

            if (!PeerEndpointCodec.TryDecode(payload.Value.Span, out var endpoint))
            {
                continue;
            }

            await udpTransport.AddPeerAsync(networkId, peerNodeId, endpoint).ConfigureAwait(false);
        }
    }

    private async Task PersistPeerAsync(
        ulong networkId,
        ulong peerNodeId,
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = BuildPeerFileKey(networkId, peerNodeId);

        Span<byte> stackBuffer = stackalloc byte[PeerEndpointCodec.MaxEncodedLength];
        if (!PeerEndpointCodec.TryEncode(endpoint, stackBuffer, out var bytesWritten))
        {
            throw new InvalidOperationException("Failed to encode peer endpoint.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(bytesWritten);
        try
        {
            stackBuffer.Slice(0, bytesWritten).CopyTo(buffer);
            await _store.WriteAsync(key, buffer.AsMemory(0, bytesWritten), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private IPEndPoint? GetLocalTransportEndpoint()
    {
        if (_transport is not OsUdpNodeTransport udpTransport)
        {
            return null;
        }

        var advertised = _options.AdvertisedTransportEndpoint;
        if (advertised is null)
        {
            return udpTransport.LocalEndpoint;
        }

        if (advertised.Port != 0)
        {
            return advertised;
        }

        return new IPEndPoint(advertised.Address, udpTransport.LocalEndpoint.Port);
    }

    private Task OnFrameReceivedAsync(ulong sourceNodeId, ulong networkId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawFrame = new RawFrame(networkId, sourceNodeId, payload);
        RawFrameReceived?.Invoke(in rawFrame);

        var frameReceived = FrameReceived;
        if (frameReceived is not null)
        {
            frameReceived.Invoke(
                this,
                new NetworkFrame(
                    networkId,
                    sourceNodeId,
                    payload,
                    DateTimeOffset.UtcNow));
        }

        RaiseEvent(EventCode.NetworkFrameReceived, DateTimeOffset.UtcNow, networkId, "Frame received");
        return Task.CompletedTask;
    }

    private static ulong ComputeNodeIdFromSecret(ReadOnlySpan<byte> secret)
    {
        var hash = SHA256.HashData(secret.Slice(0, 32));
        return BinaryPrimitives.ReadUInt64LittleEndian(hash) & NodeId.MaxValue;
    }

    private static string BuildNetworkFileKey(ulong networkId) => $"{NetworksDirectory}/{networkId}.conf";

    private static string BuildNetworkAddressesFileKey(ulong networkId)
        => $"{NetworksDirectory}/{networkId}{NetworkAddressesSuffix}";

    private static string BuildPeerFileKey(ulong networkId, ulong peerNodeId) => $"{PeersDirectory}/{networkId}/{peerNodeId}.peer";

    private static string BuildPeersNetworkPrefix(ulong networkId) => $"{PeersDirectory}/{networkId}";

    private static bool TryParsePeerKey(ReadOnlySpan<char> prefix, string key, out ulong peerNodeId)
    {
        peerNodeId = 0;
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = key.AsSpan(prefix.Length).TrimStart('/');
        if (!suffix.EndsWith(".peer", StringComparison.Ordinal))
        {
            return false;
        }

        suffix = suffix[..^5];
        if (suffix.Length == 0 || suffix.Contains('/'))
        {
            return false;
        }

        return ulong.TryParse(suffix, out peerNodeId);
    }

    private async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_state != NodeState.Running)
            {
                throw new InvalidOperationException("Node must be started.");
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task LeaveAllNetworksAsync()
    {
        await UnregisterAllNetworksAsync(CancellationToken.None).ConfigureAwait(false);
        _joinedNetworks.Clear();
    }

    private async Task UnregisterAllNetworksAsync(CancellationToken cancellationToken)
    {
        foreach (var kv in _networkRegistrations)
        {
            await _transport.LeaveNetworkAsync(kv.Key, kv.Value, cancellationToken).ConfigureAwait(false);
        }

        _networkRegistrations.Clear();
    }

    private void RaiseEvent(
        EventCode code,
        DateTimeOffset timestampUtc,
        ulong? networkId = null,
        string? message = null,
        Exception? error = null)
    {
        var handler = EventRaised;
        var channel = _events;
        if (handler is null && channel is null)
        {
            return;
        }

        var e = new NodeEvent(code, timestampUtc, networkId, message, error);
        handler?.Invoke(this, e);
        channel?.Writer.TryWrite(e);
    }
}

internal sealed class NetworkIdReadOnlyCollection : IReadOnlyCollection<ulong>
{
    private readonly ConcurrentDictionary<ulong, NetworkInfo> _source;

    public NetworkIdReadOnlyCollection(ConcurrentDictionary<ulong, NetworkInfo> source)
    {
        _source = source;
    }

    public int Count => _source.Count;

    public IEnumerator<ulong> GetEnumerator() => _source.Keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed record NetworkInfo(ulong NetworkId, DateTimeOffset JoinedAt);

public sealed record NetworkState(ulong NetworkId, DateTimeOffset JoinedAt, NetworkStateState State);

public enum NetworkStateState
{
    Joined
}

public enum NodeState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted
}
