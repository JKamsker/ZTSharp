namespace ZTSharp;

internal static class StateStorePlanetAliases
{
    public const string PlanetKey = "planet";
    public const string RootsKey = "roots";

    public static bool IsPlanetAlias(string key)
        => string.Equals(key, PlanetKey, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(key, RootsKey, StringComparison.OrdinalIgnoreCase);

    public static bool TryGetCanonicalAliasKey(string key, out string canonicalKey)
    {
        if (string.Equals(key, PlanetKey, StringComparison.OrdinalIgnoreCase))
        {
            canonicalKey = PlanetKey;
            return true;
        }

        if (string.Equals(key, RootsKey, StringComparison.OrdinalIgnoreCase))
        {
            canonicalKey = RootsKey;
            return true;
        }

        canonicalKey = string.Empty;
        return false;
    }
}
