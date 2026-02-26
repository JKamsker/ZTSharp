namespace ZTSharp;

internal static class StateStorePrefixNormalization
{
    public static string NormalizeForList(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        if (prefix.Contains('\0', StringComparison.Ordinal) || prefix.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid key prefix: {prefix}", nameof(prefix));
        }

        var normalizedPath = prefix.Replace('\\', '/');
        if (Path.IsPathRooted(normalizedPath))
        {
            throw new ArgumentException($"Invalid key prefix: {prefix}", nameof(prefix));
        }

        var normalized = normalizedPath.Trim('/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "." or "..")
            {
                throw new ArgumentException($"Invalid key prefix: {prefix}", nameof(prefix));
            }
        }

        return string.Join('/', parts);
    }
}
