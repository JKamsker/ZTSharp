namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierSocketIdentityMigration
{
    public static bool TryLoadLibztIdentity(string stateRootPath, out ZeroTierIdentity identity)
    {
        identity = default!;
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        var libztSecretPath = Path.Combine(stateRootPath, "libzt", "identity.secret");
        if (!File.Exists(libztSecretPath))
        {
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(libztSecretPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (!ZeroTierIdentity.TryParse(text, out identity))
        {
            return false;
        }

        if (identity.PrivateKey is null)
        {
            return false;
        }

        return identity.LocallyValidate();
    }
}

