using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierPlanetLoader
{
    public static ZeroTierWorld Load(ZeroTierSocketOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.PlanetSource == ZeroTierPlanetSource.EmbeddedDefault)
        {
            var embedded = ZeroTierWorldCodec.Decode(ZeroTierDefaultPlanet.World);
            ValidatePlanet(embedded);

            if (TryLoadPlanetFromState(options.StateRootPath, embedded, cancellationToken, out var fromState))
            {
                return fromState;
            }

            return embedded;
        }

        var world = options.PlanetSource switch
        {
            ZeroTierPlanetSource.FilePath => LoadFromFile(options, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Invalid PlanetSource value.")
        };

        ValidatePlanet(world);

        return world;
    }

    private static bool TryLoadPlanetFromState(
        string stateRootPath,
        ZeroTierWorld embedded,
        CancellationToken cancellationToken,
        out ZeroTierWorld world)
    {
        world = default!;
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new[]
        {
            Path.Combine(stateRootPath, "libzt", "roots"),
            Path.Combine(stateRootPath, "planet"),
            Path.Combine(stateRootPath, "roots")
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(candidate);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (info.Length == 0)
            {
                continue;
            }

            if (info.Length > ZeroTierProtocolLimits.MaxWorldBytes)
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(candidate);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var decoded = ZeroTierWorldCodec.Decode(bytes);
                if (decoded.Type != ZeroTierWorldType.Planet || decoded.Roots.Count == 0)
                {
                    continue;
                }

                if (ZeroTierWorldSignature.IsValidSignedUpdate(embedded, decoded))
                {
                    world = decoded;
                    return true;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
            }
        }

        return false;
    }

    private static ZeroTierWorld LoadFromFile(ZeroTierSocketOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PlanetFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        FileInfo info;
        try
        {
            info = new FileInfo(options.PlanetFilePath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Unable to read planet file.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Unable to read planet file.", ex);
        }

        if (info.Length == 0)
        {
            throw new InvalidOperationException("Planet file is empty.");
        }

        if (info.Length > ZeroTierProtocolLimits.MaxWorldBytes)
        {
            throw new FormatException($"Planet file is too large ({info.Length} bytes).");
        }

        var bytes = File.ReadAllBytes(options.PlanetFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        return ZeroTierWorldCodec.Decode(bytes);
    }

    private static void ValidatePlanet(ZeroTierWorld world)
    {
        if (world.Type != ZeroTierWorldType.Planet)
        {
            throw new InvalidOperationException($"Planet file must contain a planet world definition. Got: {world.Type}.");
        }

        if (world.Roots.Count == 0)
        {
            throw new InvalidOperationException("Planet file contains zero roots.");
        }
    }
}
