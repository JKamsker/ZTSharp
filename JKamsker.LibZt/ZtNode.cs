using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using JKamsker.LibZt.Transport;

namespace JKamsker.LibZt;

/// <summary>
/// Fully managed .NET node facade.
/// </summary>
public sealed class ZtNode : IAsyncDisposable
{
    private const string IdentitySecretKey = "identity.secret";
    private const string IdentityPublicKey = "identity.public";
    private const string PlanetKey = "planet";
    private const string NetworksDirectory = "networks.d";

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly IZtStateStore _store;
    private static readonly InMemoryNodeTransport SharedTransport = new();
    private readonly IZtNodeTransport _transport;
    private readonly bool _ownsTransport;
    private readonly ILogger _logger;
    private readonly ZtNodeOptions _options;
    private readonly ConcurrentDictionary<ulong, NetworkInfo> _joinedNetworks = new();
    private readonly ConcurrentDictionary<ulong, Guid> _networkRegistrations = new();
    private readonly Channel<ZtEvent> _events = Channel.CreateUnbounded<ZtEvent>();
    private readonly CancellationTokenSource _nodeCts = new();

    private ZtNodeState _state;
    private ZtNodeId _nodeId;
    private bool _disposed;

    public ZtNode(ZtNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateRootPath);

        _options = options;
        _store = options.StateStore ?? new FileZtStateStore(options.StateRootPath);
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ZtNode>();
        _transport = options.TransportMode == ZtTransportMode.OsUdp
            ? new OsUdpNodeTransport(options.UdpListenPort ?? 0)
            : SharedTransport;
        _ownsTransport = options.TransportMode == ZtTransportMode.OsUdp;
        _state = ZtNodeState.Created;
        _nodeId = default;
    }

    public event EventHandler<ZtEvent>? EventRaised;
    public event EventHandler<ZtNetworkFrame>? FrameReceived;

    public IZtStateStore Store => _store;

    public string StateRootPath => _options.StateRootPath;

    public ZtNodeId NodeId => _nodeId;

    public bool IsRunning => _state == ZtNodeState.Running;

    public ZtNodeState State => _state;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_state is ZtNodeState.Running or ZtNodeState.Starting)
            {
                return;
            }

            if (_state is ZtNodeState.Stopping)
            {
                throw new InvalidOperationException("Cannot start while stopping.");
            }

            _state = ZtNodeState.Starting;
            RaiseEvent(new ZtEvent(ZtEventCode.NodeStarting, DateTimeOffset.UtcNow));

            var identity = await EnsureIdentityAsync(cancellationToken).ConfigureAwait(false);
            _nodeId = identity.NodeId;
            await RecoverNetworksAsync(cancellationToken).ConfigureAwait(false);
            _state = ZtNodeState.Running;
            RaiseEvent(new ZtEvent(ZtEventCode.NodeStarted, DateTimeOffset.UtcNow, Message = $"Node {_nodeId} started"));
        }
        catch (OperationCanceledException)
        {
            _state = ZtNodeState.Faulted;
            throw;
        }
        catch (Exception ex)
        {
            _state = ZtNodeState.Faulted;
            _logger.LogError(ex, "Failed to start node");
            RaiseEvent(new ZtEvent(ZtEventCode.NodeFaulted, DateTimeOffset.UtcNow, Error: ex, Message = ex.Message));
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
            if (_state is ZtNodeState.Stopped or ZtNodeState.Faulted)
            {
                return;
            }

            _state = ZtNodeState.Stopping;
            RaiseEvent(new ZtEvent(ZtEventCode.NodeStopping, DateTimeOffset.UtcNow));
            await _store.FlushAsync(cancellationToken).ConfigureAwait(false);
            _state = ZtNodeState.Stopped;
            RaiseEvent(new ZtEvent(ZtEventCode.NodeStopped, DateTimeOffset.UtcNow));
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
    {
        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        RaiseEvent(new ZtEvent(ZtEventCode.NetworkJoinRequested, DateTimeOffset.UtcNow, networkId));

        var key = BuildNetworkFileKey(networkId);
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new NetworkState(networkId, now, NetworkStateState.Joined),
            JsonSerializerOptions.Default);

        await _store.WriteAsync(key, payload, cancellationToken).ConfigureAwait(false);
        _joinedNetworks[networkId] = new NetworkInfo(networkId, now);
        var registration = await _transport.JoinNetworkAsync(
            networkId,
            _nodeId.Value,
            OnFrameReceivedAsync,
            cancellationToken).ConfigureAwait(false);
        _networkRegistrations[networkId] = registration;

        RaiseEvent(new ZtEvent(ZtEventCode.NetworkJoined, DateTimeOffset.UtcNow, networkId));
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
            RaiseEvent(new ZtEvent(ZtEventCode.NetworkLeft, DateTimeOffset.UtcNow, networkId));
        }
    }

    public async Task SendFrameAsync(ulong networkId, byte[] payload, CancellationToken cancellationToken = default)
    {
        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        if (!_joinedNetworks.ContainsKey(networkId))
        {
            throw new InvalidOperationException($"Node is not a member of network {networkId}.");
        }

        await _transport.SendFrameAsync(networkId, _nodeId.Value, payload, cancellationToken).ConfigureAwait(false);
        RaiseEvent(new ZtEvent(ZtEventCode.NetworkFrameSent, DateTimeOffset.UtcNow, networkId, Message = "Frame sent"));
    }

    public Task<IReadOnlyCollection<ulong>> GetNetworksAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<ulong>>(_joinedNetworks.Keys.ToArray());
    }

    public IAsyncEnumerable<ZtEvent> GetEventStream(CancellationToken cancellationToken = default)
    {
        return _events.Reader.ReadAllAsync(cancellationToken);
    }

    public async Task<ZtIdentity> GetIdentityAsync(CancellationToken cancellationToken = default)
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

        _disposed = true;
        _nodeCts.Cancel();
        await StopAsync().ConfigureAwait(false);
        await LeaveAllNetworksAsync().ConfigureAwait(false);
        if (_ownsTransport && _transport is IAsyncDisposable asyncTransport)
        {
            await asyncTransport.DisposeAsync().ConfigureAwait(false);
        }

        _stateLock.Dispose();
        _events.Writer.TryComplete();
        _nodeCts.Dispose();
    }

    private async Task<ZtIdentity> EnsureIdentityAsync(CancellationToken cancellationToken)
    {
        var secret = await _store.ReadAsync(IdentitySecretKey, cancellationToken).ConfigureAwait(false);
        var publicKey = await _store.ReadAsync(IdentityPublicKey, cancellationToken).ConfigureAwait(false);

        if (secret is { Length: 32 } && publicKey is { Length: 32 })
        {
            return new ZtIdentity(new ZtNodeId(ComputeNodeIdFromSecret(secret)), DateTimeOffset.UtcNow, publicKey, secret);
        }

        var createdSecret = RandomNumberGenerator.GetBytes(32);
        var createdPublic = SHA512.HashData(createdSecret.AsSpan(0, 32).ToArray()).AsSpan(0, 32).ToArray();
        var identity = new ZtIdentity(
            new ZtNodeId(ComputeNodeIdFromSecret(createdSecret)),
            DateTimeOffset.UtcNow,
            createdPublic,
            createdSecret);

        await _store.WriteAsync(IdentitySecretKey, identity.SecretKey, cancellationToken).ConfigureAwait(false);
        await _store.WriteAsync(IdentityPublicKey, identity.PublicKey, cancellationToken).ConfigureAwait(false);
        await _store.WriteAsync(PlanetKey, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
        RaiseEvent(new ZtEvent(ZtEventCode.IdentityInitialized, DateTimeOffset.UtcNow));
        return identity;
    }

    private async Task RecoverNetworksAsync(CancellationToken cancellationToken)
    {
        var keys = await _store.ListAsync(NetworksDirectory, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            var relative = key;
            if (!relative.StartsWith($"{NetworksDirectory}/", StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = relative.AsSpan($"{NetworksDirectory}/".Length);
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
            var registration = await _transport.JoinNetworkAsync(
                network,
                _nodeId.Value,
                OnFrameReceivedAsync,
                cancellationToken).ConfigureAwait(false);
            _networkRegistrations[network] = registration;
        }
    }

    private Task OnFrameReceivedAsync(ulong sourceNodeId, ulong networkId, byte[] payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FrameReceived?.Invoke(this, new ZtNetworkFrame(networkId, sourceNodeId, payload, DateTimeOffset.UtcNow));
        RaiseEvent(new ZtEvent(ZtEventCode.NetworkFrameReceived, DateTimeOffset.UtcNow, networkId, Message = "Frame received"));
        return Task.CompletedTask;
    }

    private static ulong ComputeNodeIdFromSecret(byte[] secret)
    {
        var hash = SHA256.HashData(secret.AsSpan(0, 32));
        var bytes = hash.AsSpan(0, sizeof(ulong)).ToArray();
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToUInt64(bytes, 0);
    }

    private static string BuildNetworkFileKey(ulong networkId) => $"{NetworksDirectory}/{networkId}.conf";

    private async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_state != ZtNodeState.Running)
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
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ZtNode));
        }
    }

    private async Task LeaveAllNetworksAsync()
    {
        foreach (var kv in _networkRegistrations.ToArray())
        {
            await _transport.LeaveNetworkAsync(kv.Key, kv.Value).ConfigureAwait(false);
        }

        _networkRegistrations.Clear();
        _joinedNetworks.Clear();
    }

    private void RaiseEvent(ZtEvent e)
    {
        EventRaised?.Invoke(this, e);
        _events.Writer.TryWrite(e);
    }
}

public sealed record NetworkInfo(ulong NetworkId, DateTimeOffset JoinedAt);

public sealed record NetworkState(ulong NetworkId, DateTimeOffset JoinedAt, NetworkStateState State);

public enum NetworkStateState
{
    Joined
}

public enum ZtNodeState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted
}
