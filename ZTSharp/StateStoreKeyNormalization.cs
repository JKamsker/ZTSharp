namespace ZTSharp;

internal static class StateStoreKeyNormalization
{
    public static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var normalized = key.Replace('\\', '/').TrimStart('/');
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
