using System.Net;
using Microsoft.Extensions.Logging;

namespace JKamsker.LibZt;

/// <summary>
/// Configuration used to construct and start a managed node.
/// </summary>
public sealed record class ZtNodeOptions
{
    public required string StateRootPath { get; init; }

    public string? NodeName { get; init; }

    public IZtStateStore? StateStore { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    public ZtTransportMode TransportMode { get; init; } = ZtTransportMode.InMemory;

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
