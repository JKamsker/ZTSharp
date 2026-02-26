using System.Text;
using ZTSharp.Internal;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierSocketIdentityMigration
{
    private const int MaxLibztIdentitySecretBytes = 64 * 1024;

    public static bool TryLoadLibztIdentity(string stateRootPath, out ZeroTierIdentity identity)
    {
        identity = default!;
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        var libztSecretPath = Path.Combine(stateRootPath, "libzt", "identity.secret");
        if (!File.Exists(libztSecretPath))
        {
            return false;
        }

        if (!BoundedFileIO.TryReadAllText(libztSecretPath, MaxLibztIdentitySecretBytes, Encoding.UTF8, out var text))
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

