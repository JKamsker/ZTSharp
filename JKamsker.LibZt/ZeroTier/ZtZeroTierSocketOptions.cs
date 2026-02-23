using Microsoft.Extensions.Logging;

namespace JKamsker.LibZt.ZeroTier;

public sealed class ZtZeroTierSocketOptions
{
    public required string StateRootPath { get; init; }

    public required ulong NetworkId { get; init; }

    public TimeSpan JoinTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public ILoggerFactory? LoggerFactory { get; init; }

    public ZtZeroTierPlanetSource PlanetSource { get; init; } = ZtZeroTierPlanetSource.EmbeddedDefault;

    public string? PlanetFilePath { get; init; }
}

