using System.Net;
using Microsoft.Extensions.Logging;

namespace ZTSharp;

/// <summary>
/// Configuration used to construct and start a managed node.
/// </summary>
public sealed record class NodeOptions
{
    public required string StateRootPath { get; init; }

    public string? NodeName { get; init; }

    public IStateStore? StateStore { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    public TransportMode TransportMode { get; init; } = TransportMode.InMemory;

    public int? UdpListenPort { get; init; }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    public int TickIntervalMilliseconds { get; init; } = 50;

    public bool EnableIpv6 { get; init; } = true;

    public bool EnablePeerDiscovery { get; init; } = true;

    /// <summary>
    /// Optional advertised endpoint for the underlying transport (OS UDP).
    /// Use this when peers need to reach this node from another process/machine.
    /// </summary>
    public IPEndPoint? AdvertisedTransportEndpoint { get; init; }
}
