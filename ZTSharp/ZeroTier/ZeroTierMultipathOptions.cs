namespace ZTSharp.ZeroTier;

public sealed class ZeroTierMultipathOptions
{
    /// <summary>
    /// Enables experimental peer path negotiation, direct-path selection, and (future) multipath bonding.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Bonding policy used when multiple physical paths are available.
    /// </summary>
    public ZeroTierBondPolicy BondPolicy { get; init; } = ZeroTierBondPolicy.Off;

    /// <summary>
    /// Desired number of UDP sockets to open (ephemeral ports) when <see cref="Enabled"/> is true
    /// and <see cref="LocalUdpPorts"/> is not provided.
    /// </summary>
    public int UdpSocketCount { get; init; } = 1;

    /// <summary>
    /// Explicit local UDP ports to bind (use 0 to request an ephemeral port).
    /// When set, the list length must match <see cref="UdpSocketCount"/>.
    /// </summary>
    public IReadOnlyList<int>? LocalUdpPorts { get; init; }
}

public enum ZeroTierBondPolicy
{
    Off = 0,
    ActiveBackup = 1,
    Broadcast = 2,
    BalanceRoundRobin = 3,
    BalanceXor = 4,
    BalanceAware = 5
}

