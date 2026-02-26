using System.Collections.Concurrent;

namespace ZTSharp;

/// <summary>
/// Volatile in-memory state store, useful for tests and simulations.
/// </summary>
public sealed class MemoryStateStore : IStateStore
{
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
        var normalizedPrefix = StateStorePrefixNormalization.NormalizeForList(prefix);
        var normalizedPrefixWithSlash = normalizedPrefix.Length == 0 ? string.Empty : normalizedPrefix + "/";
        var keys = new List<string>();
        foreach (var key in _storage.Keys)
        {
            if (normalizedPrefixWithSlash.Length == 0 ||
                string.Equals(key, normalizedPrefix, StringComparison.Ordinal) ||
                key.StartsWith(normalizedPrefixWithSlash, StringComparison.Ordinal))
            {
                keys.Add(key);
            }
        }

        if (normalizedPrefix.Length == 0 && _storage.ContainsKey(StateStorePlanetAliases.PlanetKey))
        {
            var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            if (!keys.Contains(StateStorePlanetAliases.PlanetKey, comparer))
            {
                keys.Add(StateStorePlanetAliases.PlanetKey);
            }

            if (!keys.Contains(StateStorePlanetAliases.RootsKey, comparer))
            {
                keys.Add(StateStorePlanetAliases.RootsKey);
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static string NormalizeKey(string key)
    {
        var normalized = StateStoreKeyNormalization.NormalizeKey(key);
        if (StateStorePlanetAliases.IsPlanetAlias(normalized))
        {
            normalized = StateStorePlanetAliases.PlanetKey;
        }

        return normalized;
    }
}
