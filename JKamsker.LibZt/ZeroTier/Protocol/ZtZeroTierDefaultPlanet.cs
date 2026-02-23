namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZtZeroTierDefaultPlanet
{
    private const string WorldBase64 =
        "AQAAAAAI6skKAAABfulXYM24s4ikaSIUkaqazWbMdkze/VYDnxBnrhXmnG+0LXtV" +
        "Mw4/2qxSnAeS/XNApqohq6ikif2upEo5vy0AZZrJyBjrNgCSdjfvTRQEpE1URoSF" +
        "E3l1H6p5tMTqhQQBdeoGWGBIJALh6zQgUgAOYpAGGpvgzSk8i1Xxw9JSSAivxUki" +
        "CA41Oada3cPO8PatJg1YgpO7d4bnHvpLkFfa2YZ6/hLdBMr+nv65AMze92vHuX3t" +
        "kE6rxd8JiG2cFRSmEANsuROcwhQAGilYl4787BVxLdOUjG5rOo6JPfAf9JPR+NmA" +
        "aoYMVCBXG/AAAgRowgiGJwkGJgWYgAIAEgAAMAVxDjQAUScJd4zecZAAP2aBqZ5a" +
        "0Ylen7oz5iEtRFThaLzscRIQG/AAlW7Y6S5CiSy28uxBCIGoSrGdpQ4Sh7o9kmw6" +
        "H3VczPKZoSBwVQACBGfDZ0InCQYmBZiABAAAwwJU8ryh9wAZJwli+GWucQDiB2xX" +
        "3ocOYojX1edARAixVF78o31n93uH6eVBaMJdPvGpq/KQXqXnhcAd/yOIetQjLZXH" +
        "qP0sJxEacr0VkyLcAAIEMgf8iicJBiABSfDQ2wACAAAAAAAAAAInCcr+BOupAGxq" +
        "nR3qVcFha/4qK48P+ajKyvcDdPsfOeO++By/6+8XtyKCaKCiop00iMdSVlxslly9" +
        "ZQbsJDl8yKXZ0VKFqH8AAgRUETWbJwkGKgJuoNQFAAAAAAAAAACZkycJ";

    private static readonly byte[] _world = Convert.FromBase64String(WorldBase64);

    public static ReadOnlySpan<byte> World => _world;
}

