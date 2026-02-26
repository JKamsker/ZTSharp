
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
        var path = GetPhysicalPath(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(path));
    }

    public async Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPhysicalPath(key);
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

        var path = GetPhysicalPath(key);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await File.WriteAllBytesAsync(path, value, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPhysicalPath(key);
        cancellationToken.ThrowIfCancellationRequested();
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
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            entries.Add(Path.GetRelativePath(_rootPath, file).Replace('\\', '/'));
        }

        if (virtualPrefix.Length == 0)
        {
            var hasRootsAlias = false;
            for (var i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i], StateStorePlanetAliases.RootsKey, StringComparison.Ordinal))
                {
                    hasRootsAlias = true;
                    break;
                }
            }

            if (File.Exists(Path.Combine(_rootPath, StateStorePlanetAliases.PlanetKey)) && !hasRootsAlias)
            {
                entries.Add(StateStorePlanetAliases.RootsKey);
                hasRootsAlias = true;
            }

            if (File.Exists(Path.Combine(_rootPath, StateStorePlanetAliases.RootsKey)) && !hasRootsAlias)
            {
                entries.Add(StateStorePlanetAliases.RootsKey);
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

    private string GetPhysicalPath(string key)
    {
        var normalized = NormalizeKey(key);
        if (StateStorePlanetAliases.IsPlanetAlias(normalized))
        {
            normalized = StateStorePlanetAliases.PlanetKey;
        }

        var path = Path.Combine(_rootPath, normalized);
        path = Path.GetFullPath(path);
        if (!IsUnderRoot(path))
        {
            throw new ArgumentException($"Invalid key path: {key}", nameof(key));
        }

        return path;
    }

    private static string NormalizeKey(string key)
    {
        return StateStoreKeyNormalization.NormalizeKey(key);
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
