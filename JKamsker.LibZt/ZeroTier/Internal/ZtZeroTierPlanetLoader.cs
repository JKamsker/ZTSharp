using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal static class ZtZeroTierPlanetLoader
{
    public static ZtZeroTierWorld Load(ZtZeroTierSocketOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var world = options.PlanetSource switch
        {
            ZtZeroTierPlanetSource.EmbeddedDefault => ZtZeroTierWorldCodec.Decode(ZtZeroTierDefaultPlanet.World),
            ZtZeroTierPlanetSource.FilePath => LoadFromFile(options, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Invalid PlanetSource value.")
        };

        if (world.Type != ZtZeroTierWorldType.Planet)
        {
            throw new InvalidOperationException($"Planet file must contain a planet world definition. Got: {world.Type}.");
        }

        if (world.Roots.Count == 0)
        {
            throw new InvalidOperationException("Planet file contains zero roots.");
        }

        return world;
    }

    private static ZtZeroTierWorld LoadFromFile(ZtZeroTierSocketOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PlanetFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        var bytes = File.ReadAllBytes(options.PlanetFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        return ZtZeroTierWorldCodec.Decode(bytes);
    }
}

