namespace ZTSharp;

internal static class StateStorePlanetAliases
{
    public const string PlanetKey = "planet";
    public const string RootsKey = "roots";

    public static bool IsPlanetAlias(string key)
        => string.Equals(key, PlanetKey, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(key, RootsKey, StringComparison.OrdinalIgnoreCase);
}

