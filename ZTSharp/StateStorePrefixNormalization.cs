namespace ZTSharp;

internal static class StateStorePrefixNormalization
{
    public static string NormalizeForList(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var normalized = prefix.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == "." || part == ".."))
        {
            throw new ArgumentException($"Invalid key prefix: {prefix}", nameof(prefix));
        }

        return string.Join('/', parts);
    }
}
