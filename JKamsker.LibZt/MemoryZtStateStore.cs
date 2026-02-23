using System.Collections.Concurrent;

namespace JKamsker.LibZt;

/// <summary>
/// Volatile in-memory state store, useful for tests and simulations.
/// </summary>
public sealed class MemoryZtStateStore : IZtStateStore
{
    private static readonly string[] _planetAliases = ["planet", "roots"];
    private static readonly string _planetAlias = _planetAliases[0];
    private static readonly string _rootsAlias = _planetAliases[1];

    private readonly ConcurrentDictionary<string, byte[]> _storage = new(StringComparer.Ordinal);

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_storage.ContainsKey(NormalizeKey(key)));
    }

    public Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _storage.TryGetValue(NormalizeKey(key), out var value);
        if (value is null)
        {
            return Task.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        return Task.FromResult<ReadOnlyMemory<byte>?>(value);
    }

    public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = new byte[value.Length];
        value.CopyTo(copy);
        _storage[NormalizeKey(key)] = copy;
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
        var keys = new List<string>();
        foreach (var key in _storage.Keys)
        {
            if (key.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            {
                keys.Add(key);
            }
        }

        if (normalizedPrefix.Length == 0)
        {
            var hasRootsAlias = false;
            for (var i = 0; i < keys.Count; i++)
            {
                if (string.Equals(keys[i], _rootsAlias, StringComparison.Ordinal))
                {
                    hasRootsAlias = true;
                    break;
                }
            }

            if (_storage.ContainsKey(_planetAlias) && !hasRootsAlias)
            {
                keys.Add(_rootsAlias);
            }
        }

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

        normalized = string.Join('/', parts);
        if (_planetAliases.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            normalized = _planetAlias;
        }

        return normalized;
    }
}
