using System.Net;
using Microsoft.Extensions.Logging;
using ZTSharp.Transport;

namespace ZTSharp.Internal;

internal sealed class NodeCore : IAsyncDisposable
{
    private static readonly InMemoryNodeTransport SharedTransport = new();

    private readonly NodeRuntimeState _runtime = new();

    private readonly NodeOptions _options;
    private readonly IStateStore _store;
    private readonly INodeTransport _transport;
    private readonly ILogger _logger;

    private readonly NodeEventStream _events;
    private readonly NodeTransportService _transportService;
    private readonly NodeIdentityService _identityService;
    private readonly NodePeerService _peerService;
    private readonly NodeNetworkService _networkService;
    private readonly NodeLifecycleService _lifecycleService;

    public NodeCore(
        NodeOptions options,
        Action<NodeEvent> onEventRaised,
        Action<NetworkFrame> onFrameReceived,
        RawFrameReceivedHandler onRawFrameReceived)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateRootPath);

        _options = options;
        _store = options.StateStore ?? new FileStateStore(options.StateRootPath);
        _logger = (options.LoggerFactory ?? global::Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger<Node>();
        _transport = options.TransportMode == TransportMode.OsUdp
            ? new OsUdpNodeTransport(options.UdpListenPort ?? 0, options.EnableIpv6, options.EnablePeerDiscovery)
            : SharedTransport;

        _events = new NodeEventStream(onEventRaised);
        _transportService = new NodeTransportService(_events, _logger, onFrameReceived, onRawFrameReceived, _transport, _options);
        _identityService = new NodeIdentityService(_store, _events);
        _peerService = new NodePeerService(_store);
        _networkService = new NodeNetworkService(_store, _transport, _events, _peerService);

        var stateLock = new SemaphoreSlim(1, 1);
        var nodeCts = new CancellationTokenSource();
        _lifecycleService = new NodeLifecycleService(
            _runtime,
            stateLock,
            nodeCts,
            _transport,
            _store,
            _logger,
            _events,
            _identityService,
            _networkService,
            _transportService,
            ownsTransport: options.TransportMode == TransportMode.OsUdp);
    }

    public IStateStore Store => _store;

    public string StateRootPath => _options.StateRootPath;

    public IPEndPoint? LocalTransportEndpoint => _transportService.GetLocalTransportEndpoint();

    public NodeId NodeId => _runtime.NodeId;

    public bool IsRunning => _runtime.State == NodeState.Running;

    public NodeState State => _runtime.State;

    public Task StartAsync(CancellationToken cancellationToken = default) => _lifecycleService.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _lifecycleService.StopAsync(cancellationToken);

    public Task JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
        => _lifecycleService.ExecuteWhileRunningAsync(
            async token =>
            {
                var localEndpoint = _transportService.GetLocalTransportEndpoint();
                await _networkService
                    .JoinNetworkAsync(networkId, _runtime.NodeId.Value, localEndpoint, _transportService.OnFrameReceivedAsync, token)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    public Task LeaveNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
        => _lifecycleService.ExecuteWhileRunningAsync(
            token => _networkService.LeaveNetworkAsync(networkId, token),
            cancellationToken);

    public Task SendFrameAsync(ulong networkId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => _lifecycleService.ExecuteWhileRunningAsync(
            token => _networkService.SendFrameAsync(networkId, _runtime.NodeId.Value, payload, token),
            cancellationToken);

    public Task<IReadOnlyCollection<ulong>> GetNetworksAsync(CancellationToken cancellationToken = default)
        => _networkService.GetNetworksAsync(cancellationToken);

    public Task<IReadOnlyList<NetworkAddress>> GetNetworkAddressesAsync(ulong networkId, CancellationToken cancellationToken = default)
        => _lifecycleService.ExecuteWhileRunningAsync(
            token => _networkService.GetNetworkAddressesAsync(networkId, token),
            cancellationToken);

    public async Task SetNetworkAddressesAsync(
        ulong networkId,
        IReadOnlyList<NetworkAddress> addresses,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleService.ExecuteWhileRunningAsync(
            token => _networkService.SetNetworkAddressesAsync(networkId, addresses, token),
            cancellationToken).ConfigureAwait(false);
    }

    public Task AddPeerAsync(ulong networkId, ulong peerNodeId, IPEndPoint endpoint, CancellationToken cancellationToken = default)
        => _peerService.AddPeerAsync(networkId, peerNodeId, endpoint, _transport, cancellationToken);

    public Task<Identity> GetIdentityAsync(CancellationToken cancellationToken = default)
        => _lifecycleService.ExecuteWhileRunningAsync(
            token => _identityService.EnsureIdentityAsync(token),
            cancellationToken);

    public IAsyncEnumerable<NodeEvent> GetEventStream(CancellationToken cancellationToken = default)
        => _events.GetEventStream(cancellationToken);

    public ValueTask DisposeAsync() => _lifecycleService.DisposeAsync();
}
