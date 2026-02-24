using Microsoft.Extensions.Logging;

namespace ZTSharp.ZeroTier;

public sealed class ZeroTierSocketOptions
{
    public required string StateRootPath { get; init; }

    public required ulong NetworkId { get; init; }

    public TimeSpan JoinTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public ILoggerFactory? LoggerFactory { get; init; }

    public ZeroTierPlanetSource PlanetSource { get; init; } = ZeroTierPlanetSource.EmbeddedDefault;

    public string? PlanetFilePath { get; init; }
}

