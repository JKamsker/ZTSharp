namespace JKamsker.LibZt.Transport;

/// <summary>
/// Internal transport abstraction for managed node-to-node simulation and future adapters.
/// </summary>
internal interface IZtNodeTransport
{
    Task<Guid> JoinNetworkAsync(
        ulong networkId,
        ulong nodeId,
        Func<ulong, ulong, byte[], CancellationToken, Task> onFrameReceived,
        CancellationToken cancellationToken = default);

    Task LeaveNetworkAsync(ulong networkId, Guid registrationId, CancellationToken cancellationToken = default);

    Task SendFrameAsync(
        ulong networkId,
        ulong sourceNodeId,
        byte[] payload,
        CancellationToken cancellationToken = default);

    Task FlushAsync(CancellationToken cancellationToken = default);
}
