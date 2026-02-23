namespace JKamsker.LibZt.Libzt;

/// <summary>
/// Options for an upstream <c>libzt</c> node (via the <c>ZeroTier.Sockets</c> package).
/// </summary>
public sealed class ZtLibztNodeOptions
{
    /// <summary>
    /// Storage directory for libzt state (identity, networks, peers, planet, ...).
    /// </summary>
    public required string StoragePath { get; init; }

    /// <summary>
    /// UDP random port range start (inclusive).
    /// </summary>
    public ushort RandomPortRangeStart { get; init; } = 40_000;

    /// <summary>
    /// UDP random port range end (inclusive).
    /// </summary>
    public ushort RandomPortRangeEnd { get; init; } = 50_000;

    public bool AllowNetworkCaching { get; init; }

    public bool AllowPeerCaching { get; init; } = true;

    /// <summary>
    /// Optional event sink for libzt events.
    /// </summary>
    public Action<ZeroTier.Core.Event>? EventSink { get; init; }
}

