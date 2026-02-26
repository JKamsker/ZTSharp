using ZTSharp.Sockets;

namespace ZTSharp.Http;

public sealed class OverlayHttpMessageHandlerOptions
{
    /// <summary>
    /// Optional mapping of IP addresses to node ids.
    /// </summary>
    public OverlayAddressBook? AddressBook { get; init; }

    /// <summary>
    /// Optional custom resolver for mapping the request host to a node id.
    /// When provided, it is consulted before <see cref="AddressBook"/> and the built-in node id parsing.
    /// </summary>
    public Func<string, ulong?>? HostResolver { get; init; }

    public int LocalPortStart { get; init; } = 49152;

    public int LocalPortEnd { get; init; } = 65535;
}

