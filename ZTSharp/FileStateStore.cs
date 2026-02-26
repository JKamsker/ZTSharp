
namespace ZTSharp;

/// <summary>
/// Stores node state in a directory hierarchy using logical file names.
/// </summary>
public sealed class FileStateStore : IStateStore
{
    private readonly string _rootPath;
    private readonly StringComparison _pathComparison;
    private readonly string _rootPathPrefix;
    private readonly string _rootPathTrimmed;

    public FileStateStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _rootPathTrimmed = Path.TrimEndingDirectorySeparator(_rootPath);
        _rootPathPrefix = _rootPathTrimmed + Path.DirectorySeparatorChar;
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Directory.CreateDirectory(_rootPath);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = NormalizeKey(key);
        if (StateStorePlanetAliases.IsPlanetAlias(normalized))
        {
            var planetPath = GetPhysicalPathForNormalizedKey(StateStorePlanetAliases.PlanetKey, key);
            if (File.Exists(planetPath))
            {
                return Task.FromResult(true);
            }

            var rootsPath = GetPhysicalPathForNormalizedKey(StateStorePlanetAliases.RootsKey, key);
            return Task.FromResult(File.Exists(rootsPath));
        }

        var path = GetPhysicalPathForNormalizedKey(normalized, key);
        return Task.FromResult(File.Exists(path));
    }

    public async Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPhysicalPathForRead(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = NormalizeKey(key);
        if (StateStorePlanetAliases.IsPlanetAlias(normalized))
        {
            normalized = StateStorePlanetAliases.PlanetKey;
        }

        var path = GetPhysicalPathForNormalizedKey(normalized, key);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await File.WriteAllBytesAsync(path, value, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = NormalizeKey(key);
        if (StateStorePlanetAliases.IsPlanetAlias(normalized))
        {
            var planetPath = GetPhysicalPathForNormalizedKey(StateStorePlanetAliases.PlanetKey, key);
            if (File.Exists(planetPath))
            {
                File.Delete(planetPath);
                return Task.FromResult(true);
            }

            var rootsPath = GetPhysicalPathForNormalizedKey(StateStorePlanetAliases.RootsKey, key);
            if (!File.Exists(rootsPath))
            {
                return Task.FromResult(false);
            }

            File.Delete(rootsPath);
            return Task.FromResult(true);
        }

        var path = GetPhysicalPathForNormalizedKey(normalized, key);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var virtualPrefix = StateStorePrefixNormalization.NormalizeForList(prefix);

        if (!Directory.Exists(_rootPath))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        if (virtualPrefix.Length != 0 &&
            StateStorePlanetAliases.TryGetCanonicalAliasKey(virtualPrefix, out var canonicalAliasPrefix))
        {
            var planetPath = Path.Combine(_rootPath, StateStorePlanetAliases.PlanetKey);
            var rootsPath = Path.Combine(_rootPath, StateStorePlanetAliases.RootsKey);
            if (File.Exists(planetPath) || File.Exists(rootsPath))
            {
                return Task.FromResult<IReadOnlyList<string>>([canonicalAliasPrefix]);
            }

            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var path = virtualPrefix.Length == 0 ? _rootPath : Path.Combine(_rootPath, virtualPrefix);
        path = Path.GetFullPath(path);
        if (!IsUnderRoot(path))
        {
            throw new ArgumentException($"Invalid key prefix: {prefix}", nameof(prefix));
        }

        if (virtualPrefix.Length != 0 && File.Exists(path))
        {
            return Task.FromResult<IReadOnlyList<string>>([virtualPrefix]);
        }

        if (!Directory.Exists(path))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var entries = new List<string>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_rootPath, file).Replace('\\', '/');
            if (StateStorePlanetAliases.TryGetCanonicalAliasKey(relativePath, out var canonicalAliasKey))
            {
                relativePath = canonicalAliasKey;
            }

            if (seen.Add(relativePath))
            {
                entries.Add(relativePath);
            }
        }

        if (virtualPrefix.Length == 0)
        {
            var planetPath = Path.Combine(_rootPath, StateStorePlanetAliases.PlanetKey);
            var rootsPath = Path.Combine(_rootPath, StateStorePlanetAliases.RootsKey);
            if (File.Exists(planetPath) || File.Exists(rootsPath))
            {
                if (seen.Add(StateStorePlanetAliases.PlanetKey))
                {
                    entries.Add(StateStorePlanetAliases.PlanetKey);
                }

                if (seen.Add(StateStorePlanetAliases.RootsKey))
                {
                    entries.Add(StateStorePlanetAliases.RootsKey);
                }
            }

            return Task.FromResult<IReadOnlyList<string>>(entries);
        }

        return Task.FromResult<IReadOnlyList<string>>(entries);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private string GetPhysicalPathForRead(string key)
    {
        var normalized = NormalizeKey(key);
        if (!StateStorePlanetAliases.IsPlanetAlias(normalized))
        {
            return GetPhysicalPathForNormalizedKey(normalized, key);
        }

        var planetPath = GetPhysicalPathForNormalizedKey(StateStorePlanetAliases.PlanetKey, key);
        if (File.Exists(planetPath))
        {
            return planetPath;
        }

        var rootsPath = GetPhysicalPathForNormalizedKey(StateStorePlanetAliases.RootsKey, key);
        if (!File.Exists(rootsPath))
        {
            return planetPath;
        }

        TryMigrateRootsToPlanet(rootsPath, planetPath);
        return File.Exists(planetPath) ? planetPath : rootsPath;
    }

    private static string NormalizeKey(string key)
    {
        return StateStoreKeyNormalization.NormalizeKey(key);
    }

    private string GetPhysicalPathForNormalizedKey(string normalizedKey, string originalKey)
    {
        var path = Path.Combine(_rootPath, normalizedKey);
        path = Path.GetFullPath(path);
        if (!IsUnderRoot(path))
        {
            throw new ArgumentException($"Invalid key path: {originalKey}", nameof(originalKey));
        }

        return path;
    }

    private static void TryMigrateRootsToPlanet(string rootsPath, string planetPath)
    {
        try
        {
            if (File.Exists(planetPath) || !File.Exists(rootsPath))
            {
                return;
            }

            File.Move(rootsPath, planetPath, overwrite: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private bool IsUnderRoot(string fullPath)
    {
        if (string.Equals(fullPath, _rootPathTrimmed, _pathComparison))
        {
            return true;
        }

        return fullPath.StartsWith(_rootPathPrefix, _pathComparison);
    }

}
