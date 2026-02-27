
namespace ZTSharp;

/// <summary>
/// Stores node state in a directory hierarchy using logical file names.
/// </summary>
public sealed class FileStateStore : IStateStore
{
    private const long MaxReadBytes = 64L * 1024 * 1024;

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
        ThrowIfRootPathIsReparsePoint();
        Directory.CreateDirectory(_rootPath);
        ThrowIfRootPathIsReparsePoint();
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

        var bytes = await ReadAllBytesWithSharingAsync(path, cancellationToken).ConfigureAwait(false);
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
        EnsureParentDirectoryExistsNoReparse(path);
        if (string.Equals(normalized, Internal.NodeStoreKeys.IdentitySecretKey, StringComparison.OrdinalIgnoreCase))
        {
            await Internal.AtomicFile.WriteSecretBytesAsync(path, value, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Internal.AtomicFile.WriteAllBytesAsync(path, value, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = NormalizeKey(key);
        if (StateStorePlanetAliases.IsPlanetAlias(normalized))
        {
            var planetPath = GetPhysicalPathForNormalizedKey(StateStorePlanetAliases.PlanetKey, key);
            var rootsPath = GetPhysicalPathForNormalizedKey(StateStorePlanetAliases.RootsKey, key);
            var deleted = false;

            if (File.Exists(planetPath))
            {
                File.Delete(planetPath);
                deleted = true;
            }

            if (File.Exists(rootsPath))
            {
                File.Delete(rootsPath);
                deleted = true;
            }

            return Task.FromResult(deleted);
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
            var planetPath = GetPhysicalPathForNormalizedKey(StateStorePlanetAliases.PlanetKey, StateStorePlanetAliases.PlanetKey);
            var rootsPath = GetPhysicalPathForNormalizedKey(StateStorePlanetAliases.RootsKey, StateStorePlanetAliases.RootsKey);
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

        ThrowIfPathTraversesReparsePoint(path);

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
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var file in Directory.EnumerateFiles(path, "*", options))
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

    private static async Task<byte[]> ReadAllBytesWithSharingAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > MaxReadBytes)
        {
            throw new IOException($"State file exceeds maximum supported size of {MaxReadBytes} bytes.");
        }

        var initialCapacity = stream.Length <= int.MaxValue
            ? (int)Math.Min(stream.Length, MaxReadBytes)
            : 0;

        using var memory = initialCapacity > 0 ? new MemoryStream(initialCapacity) : new MemoryStream();
        var buffer = new byte[16 * 1024];
        long totalRead = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            var remaining = MaxReadBytes - totalRead;
            if (read > remaining)
            {
                if (remaining > 0)
                {
                    await memory.WriteAsync(buffer.AsMemory(0, (int)remaining), cancellationToken).ConfigureAwait(false);
                }

                throw new IOException($"State file exceeds maximum supported size of {MaxReadBytes} bytes.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;
        }

        return memory.ToArray();
    }

    private string GetPhysicalPathForNormalizedKey(string normalizedKey, string originalKey)
    {
        var path = Path.Combine(_rootPath, normalizedKey);
        path = Path.GetFullPath(path);
        if (!IsUnderRoot(path))
        {
            throw new ArgumentException($"Invalid key path: {originalKey}", nameof(originalKey));
        }

        ThrowIfPathTraversesReparsePoint(path);
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

    private void ThrowIfPathTraversesReparsePoint(string fullPath)
    {
        ThrowIfRootPathIsReparsePoint();

        if (string.Equals(fullPath, _rootPathTrimmed, _pathComparison))
        {
            return;
        }

        if (!IsUnderRoot(fullPath))
        {
            throw new ArgumentException("Path is not under state root.", nameof(fullPath));
        }

        var relative = Path.GetRelativePath(_rootPathTrimmed, fullPath);
        if (relative == "." || relative.Length == 0)
        {
            return;
        }

        var current = _rootPathTrimmed;
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (!TryGetAttributes(current, out var attributes))
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("State path traversal via symlink/junction/reparse point is not allowed.");
            }
        }
    }

    private void ThrowIfRootPathIsReparsePoint()
    {
        if (IsReparsePoint(_rootPathTrimmed))
        {
            throw new InvalidOperationException("State root path must not be a symlink/junction/reparse point.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var current = Path.GetDirectoryName(_rootPathTrimmed);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (IsReparsePoint(current))
            {
                throw new InvalidOperationException("State root path must not be under a symlink/junction/reparse point.");
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, _pathComparison))
            {
                break;
            }

            current = parent;
        }
    }

    private void EnsureParentDirectoryExistsNoReparse(string fullPath)
    {
        ThrowIfRootPathIsReparsePoint();

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || string.Equals(directory, _rootPathTrimmed, _pathComparison))
        {
            return;
        }

        directory = Path.GetFullPath(directory);
        if (!IsUnderRoot(directory))
        {
            throw new ArgumentException("Path is not under state root.", nameof(fullPath));
        }

        var relative = Path.GetRelativePath(_rootPathTrimmed, directory);
        if (relative == "." || relative.Length == 0)
        {
            return;
        }

        var current = _rootPathTrimmed;
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (Directory.Exists(current))
            {
                if (IsReparsePoint(current))
                {
                    throw new InvalidOperationException("State path traversal via symlink/junction/reparse point is not allowed.");
                }

                continue;
            }

            Directory.CreateDirectory(current);
            if (IsReparsePoint(current))
            {
                throw new InvalidOperationException("State path traversal via symlink/junction/reparse point is not allowed.");
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }

        attributes = default;
        return false;
    }

}
