namespace ZTSharp;

internal static class StateStoreKeyNormalization
{
    public static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (key.Contains('\0', StringComparison.Ordinal) || key.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid key path: {key}", nameof(key));
        }

        var normalizedPath = key.Replace('\\', '/');
        if (Path.IsPathRooted(normalizedPath))
        {
            throw new ArgumentException($"Invalid key path: {key}", nameof(key));
        }

        var normalized = normalizedPath.TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "." or "..")
            {
                throw new ArgumentException($"Invalid key path: {key}", nameof(key));
            }
        }

        return string.Join('/', parts);
    }
}
