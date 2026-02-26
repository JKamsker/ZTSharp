using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierSocketFactory
{
    public static Task<ZeroTierSocket> CreateAsync(ZeroTierSocketOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateRootPath);
        ArgumentOutOfRangeException.ThrowIfZero(options.NetworkId);
        if (options.JoinTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "JoinTimeout must be positive.");
        }

        if (options.PlanetSource == ZeroTierPlanetSource.FilePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PlanetFilePath);
            if (!File.Exists(options.PlanetFilePath))
            {
                throw new FileNotFoundException("Planet file not found.", options.PlanetFilePath);
            }
        }

        if (options.PlanetSource != ZeroTierPlanetSource.EmbeddedDefault &&
            options.PlanetSource != ZeroTierPlanetSource.FilePath)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid PlanetSource value.");
        }

        var normalizedStateRootPath = Path.GetFullPath(options.StateRootPath);
        var normalizedOptions = new ZeroTierSocketOptions
        {
            StateRootPath = normalizedStateRootPath,
            NetworkId = options.NetworkId,
            JoinTimeout = options.JoinTimeout,
            LoggerFactory = options.LoggerFactory,
            PlanetSource = options.PlanetSource,
            PlanetFilePath = options.PlanetFilePath
        };

        var statePath = Path.Combine(normalizedOptions.StateRootPath, "zerotier");
        Directory.CreateDirectory(statePath);

        var identityPath = Path.Combine(statePath, "identity.bin");
        if (!ZeroTierIdentityStore.TryLoad(identityPath, out var identity))
        {
            if (!File.Exists(identityPath) &&
                ZeroTierSocketIdentityMigration.TryLoadLibztIdentity(normalizedOptions.StateRootPath, out identity))
            {
                ZeroTierIdentityStore.Save(identityPath, identity);
            }
            else
            {
                identity = ZeroTierIdentityGenerator.Generate(cancellationToken);
                ZeroTierIdentityStore.Save(identityPath, identity);
            }
        }
        else if (!identity.LocallyValidate())
        {
            throw new InvalidOperationException($"Invalid identity at '{identityPath}'. Delete it to regenerate.");
        }

        var planet = ZeroTierPlanetLoader.Load(normalizedOptions, cancellationToken);

        return Task.FromResult(new ZeroTierSocket(normalizedOptions, statePath, identity, planet));
    }
}

