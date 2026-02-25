using System.Net;
using ZTSharp.Internal;

namespace ZTSharp;

/// <summary>
/// Fully managed .NET node facade.
/// </summary>
public sealed class Node : IAsyncDisposable
{
    private readonly NodeCore _core;

    public Node(NodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateRootPath);

        _core = new NodeCore(
            options,
            RaiseEvent,
            RaiseFrame,
            RaiseRawFrame);
    }

    public event EventHandler<NodeEvent>? EventRaised;
    public event EventHandler<NetworkFrame>? FrameReceived;
    internal event RawFrameReceivedHandler? RawFrameReceived;

    public IStateStore Store => _core.Store;

    public string StateRootPath => _core.StateRootPath;

    public IPEndPoint? LocalTransportEndpoint => _core.LocalTransportEndpoint;

    public NodeId NodeId => _core.NodeId;

    public bool IsRunning => _core.IsRunning;

    public NodeState State => _core.State;

    public Task StartAsync(CancellationToken cancellationToken = default) => _core.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _core.StopAsync(cancellationToken);

    public Task JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
        => _core.JoinNetworkAsync(networkId, cancellationToken);

    public Task LeaveNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
        => _core.LeaveNetworkAsync(networkId, cancellationToken);

    public Task SendFrameAsync(ulong networkId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => _core.SendFrameAsync(networkId, payload, cancellationToken);

    public Task<IReadOnlyCollection<ulong>> GetNetworksAsync(CancellationToken cancellationToken = default)
        => _core.GetNetworksAsync(cancellationToken);

    public Task<IReadOnlyList<NetworkAddress>> GetNetworkAddressesAsync(ulong networkId, CancellationToken cancellationToken = default)
        => _core.GetNetworkAddressesAsync(networkId, cancellationToken);

    public Task SetNetworkAddressesAsync(
        ulong networkId,
        IReadOnlyList<NetworkAddress> addresses,
        CancellationToken cancellationToken = default)
        => _core.SetNetworkAddressesAsync(networkId, addresses, cancellationToken);

    public Task AddPeerAsync(ulong networkId, ulong peerNodeId, IPEndPoint endpoint, CancellationToken cancellationToken = default)
        => _core.AddPeerAsync(networkId, peerNodeId, endpoint, cancellationToken);

    public Task<Identity> GetIdentityAsync(CancellationToken cancellationToken = default)
        => _core.GetIdentityAsync(cancellationToken);

    public IAsyncEnumerable<NodeEvent> GetEventStream(CancellationToken cancellationToken = default)
        => _core.GetEventStream(cancellationToken);

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    private void RaiseEvent(NodeEvent e) => EventRaised?.Invoke(this, e);

    private void RaiseFrame(NetworkFrame frame) => FrameReceived?.Invoke(this, frame);

    private void RaiseRawFrame(in RawFrame frame) => RawFrameReceived?.Invoke(in frame);
}
