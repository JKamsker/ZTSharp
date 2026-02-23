
namespace JKamsker.LibZt;

/// <summary>
/// Stores node state in a directory hierarchy using logical file names.
/// </summary>
public sealed class FileZtStateStore : IZtStateStore
{
    private static readonly string[] _planetAliases = ["planet", "roots"];
    private readonly string _rootPath;

    public FileZtStateStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = rootPath;
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
        var virtualPrefix = NormalizePrefix(prefix);

        if (!Directory.Exists(_rootPath))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var dir = Path.Combine(_rootPath, virtualPrefix);
        if (!Directory.Exists(dir))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var entries = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_rootPath, path).Replace('\\', '/'))
            .ToArray();

        if (virtualPrefix.Length == 0)
        {
            var aliases = new List<string>(entries);
            if (File.Exists(Path.Combine(_rootPath, "planet")) && !entries.Contains("roots", StringComparer.Ordinal))
            {
                aliases.Add("roots");
            }

            if (File.Exists(Path.Combine(_rootPath, "roots")))
            {
                aliases.Add("roots");
            }

            return Task.FromResult<IReadOnlyList<string>>(aliases.Distinct(StringComparer.Ordinal).ToArray());
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
        if (_planetAliases.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            normalized = "planet";
        }

        return Path.Combine(_rootPath, normalized);
    }

    private static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == "." || part == ".."))
        {
            throw new ArgumentException($"Invalid key path: {key}", nameof(key));
        }

        return string.Join('/', parts);
    }

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var normalized = prefix.Replace('\\', '/').Trim('/');
        return normalized;
    }
}
