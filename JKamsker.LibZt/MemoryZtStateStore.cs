using System.Collections.Concurrent;

namespace JKamsker.LibZt;

/// <summary>
/// Volatile in-memory state store, useful for tests and simulations.
/// </summary>
public sealed class MemoryZtStateStore : IZtStateStore
{
    private readonly ConcurrentDictionary<string, byte[]> _storage = new(StringComparer.Ordinal);

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_storage.ContainsKey(NormalizeKey(key)));
    }

    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _storage.TryGetValue(NormalizeKey(key), out var value);
        return Task.FromResult<byte[]?>(value);
    }

    public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _storage[NormalizeKey(key)] = value.ToArray();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_storage.TryRemove(NormalizeKey(key), out _));
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPrefix = NormalizePrefix(prefix);
        var keys = _storage.Keys
            .Where(key => key.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static string NormalizePrefix(string prefix)
    {
        return string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Replace('\\', '/');
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
}
